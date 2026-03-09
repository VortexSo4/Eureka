// ============================================================
//  VulkanAnimationEngine.cs
//  EurekaSharp — Vulkan Backend
//
//  Полный аналог AnimationEngine.cs, но на Vulkan.
//
//  Что здесь:
//    ─ Создание compute pipelines (anim + morph) из SPIR-V
//    ─ Создание graphics pipeline (render)
//    ─ DescriptorSet layout + pool + sets для всех SSBO
//    ─ Dispatch compute shaders (vkCmdDispatch)
//    ─ Draw loop (vkCmdDraw per primitive)
//    ─ DynOverrides API (идентичен AnimationEngine)
//    ─ NotifySwapchainRecreated (пересоздаёт pipeline при resize)
//
//  SSBO binding points (идентичны шейдерам из AnimationEngine):
//    0 = AnimEntries
//    1 = AnimIndex
//    2 = MorphDesc
//    3 = Geometry
//    4 = RenderInstances
//
//  Шейдеры:
//    Твои GLSL шейдеры из AnimationEngine перекомпилируются в SPIR-V:
//      glslangValidator -V anim_compute.glsl  -o anim_compute.spv
//      glslangValidator -V morph_compute.glsl -o morph_compute.spv
//      glslangValidator -V render.vert        -o render.vert.spv
//      glslangValidator -V render.frag        -o render.frag.spv
//    Пути задаются через ShaderPath.
//
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using Silk.NET.Vulkan;

namespace PhysicsSimulation.Rendering.Vulkan
{
    public sealed unsafe class VulkanAnimationEngine : IDisposable
    {
        // ── Binding slots (должны совпадать с шейдерами) ─────────────────────
        private const uint BINDING_ANIM_ENTRIES  = 0;
        private const uint BINDING_ANIM_INDEX    = 1;
        private const uint BINDING_MORPH_DESC    = 2;
        private const uint BINDING_GEOMETRY      = 3;
        private const uint BINDING_RENDER_INST   = 4;

        // ── Размеры структур (те же что в AnimationEngine) ───────────────────
        private const int ANIM_ENTRY_SIZE    = 80;
        private const int ANIM_INDEX_SIZE    = 16;
        private const int MORPH_DESC_SIZE    = 32;
        private const int RENDER_INST_SIZE   = 96;

        // ── Vulkan ресурсы ────────────────────────────────────────────────────
        private readonly VulkanContext         _ctx;
        private readonly VulkanMemoryAllocator _vma;
        private readonly GeometryArena         _arena;
        private readonly List<PrimitiveGpu>    _primitives;

        // Буферы (заменяют GL SSBOs)
        private VulkanBuffer _bufAnimEntries    = null!;
        private VulkanBuffer _bufAnimIndex      = null!;
        private VulkanBuffer _bufMorphDesc      = null!;
        private VulkanBuffer _bufGeometry       = null!;
        private VulkanBuffer _bufRenderInstances = null!;
        private VulkanBuffer _bufUniforms        = null!;  // aspectRatio, time

        // Pipelines
        private Pipeline     _animComputePipeline;
        private Pipeline     _morphComputePipeline;
        private Pipeline     _graphicsPipeline;
        private PipelineLayout _computeLayout;
        private PipelineLayout _graphicsLayout;

        // Descriptors
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool      _descriptorPool;
        private DescriptorSet       _descriptorSet;

        // CPU mirrors
        private MorphDescCpu[]      _morphDescs      = [];
        private RenderInstanceCpu[] _renderInstances = [];
        private List<AnimEntryCpu>  _uploadedAnimEntries = new();

        // Config
        public float AspectRatio { get; set; } = 16f / 9f;

        // Путь к SPIR-V шейдерам (настрой под свой проект)
        public static string ShaderPath { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Vulkan");

        private bool _disposed;

        // ── DynOverride (идентичен AnimationEngine) ───────────────────────────
        public struct DynOverride
        {
            public int     Pid;
            public float   PosX, PosY;
            public float   Rotation, Scale;
            public Vector4 Color;
            public bool    HasPos, HasRot, HasScale, HasColor;
        }

        // ─────────────────────────────────────────────────────────────────────
        public VulkanAnimationEngine(
            VulkanContext ctx,
            VulkanMemoryAllocator vma,
            GeometryArena arena,
            IEnumerable<PrimitiveGpu> primitives)
        {
            _ctx        = ctx;
            _vma        = vma;
            _arena      = arena;
            _primitives = primitives.OrderBy(p => p.PrimitiveId).ToList();

            // Назначаем PrimitiveId если не назначены
            for (int i = 0; i < _primitives.Count; i++)
                if (_primitives[i].PrimitiveId == -1)
                    _primitives[i].PrimitiveId = i;

            _morphDescs      = new MorphDescCpu[_primitives.Count];
            _renderInstances = new RenderInstanceCpu[_primitives.Count];

            CreateBuffers();
            CreateDescriptorSetLayout();
            CreateDescriptorPool();
            AllocateAndUpdateDescriptorSets();
            CreateComputePipelines();
            CreateGraphicsPipeline();
            InitMorphDescs();
            InitRenderInstances();
        }

        // ── Создание буферов ──────────────────────────────────────────────────

        private void CreateBuffers()
        {
            int count = _primitives.Count;

            // Минимум 1 элемент — Vulkan не допускает буферы размером 0
            int safeCount = Math.Max(1, count);

            _bufAnimEntries     = _vma.CreateStorageBuffer((ulong)(512 * ANIM_ENTRY_SIZE));
            _bufAnimIndex       = _vma.CreateStorageBuffer((ulong)(safeCount * ANIM_INDEX_SIZE));
            _bufMorphDesc       = _vma.CreateStorageBuffer((ulong)(safeCount * MORPH_DESC_SIZE));
            _bufGeometry        = _vma.CreateVertexBuffer(256 * 1024); // 256 KB начально, растёт по мере надобности
            _bufRenderInstances = _vma.CreateStorageBuffer((ulong)(safeCount * RENDER_INST_SIZE));
            _bufUniforms        = _vma.CreateUniformBuffer((ulong)sizeof(FramePushConstants));

            DebugManager.Memory($"VulkanAnimationEngine: Буферы созданы ({count} примитивов).");
        }

        // ── Descriptor Set Layout ─────────────────────────────────────────────

        private void CreateDescriptorSetLayout()
        {
            // 5 SSBO (binding 0..4) + 1 UBO (binding 5)
            var bindings = new DescriptorSetLayoutBinding[]
            {
                MakeBinding(BINDING_ANIM_ENTRIES,  DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_ANIM_INDEX,    DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_MORPH_DESC,    DescriptorType.StorageBuffer, ShaderStageFlags.ComputeBit),
                MakeBinding(BINDING_GEOMETRY,      DescriptorType.StorageBuffer,
                    ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit),
                MakeBinding(BINDING_RENDER_INST,   DescriptorType.StorageBuffer,
                    ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit),
                MakeBinding(5, DescriptorType.UniformBuffer,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit)
            };

            fixed (DescriptorSetLayoutBinding* p = bindings)
            {
                var info = new DescriptorSetLayoutCreateInfo
                {
                    SType        = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)bindings.Length,
                    PBindings    = p
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, &info, null, out _descriptorSetLayout),
                    "CreateDescriptorSetLayout");
            }
        }

        private static DescriptorSetLayoutBinding MakeBinding(
            uint binding, DescriptorType type, ShaderStageFlags stages) =>
            new()
            {
                Binding            = binding,
                DescriptorType     = type,
                DescriptorCount    = 1,
                StageFlags         = stages,
                PImmutableSamplers = null
            };

        // ── Descriptor Pool + Set ─────────────────────────────────────────────

        private void CreateDescriptorPool()
        {
            var sizes = new DescriptorPoolSize[]
            {
                new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 5 },
                new() { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 }
            };

            fixed (DescriptorPoolSize* p = sizes)
            {
                var info = new DescriptorPoolCreateInfo
                {
                    SType         = StructureType.DescriptorPoolCreateInfo,
                    MaxSets       = 1,
                    PoolSizeCount = (uint)sizes.Length,
                    PPoolSizes    = p
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateDescriptorPool(_ctx.Device, &info, null, out _descriptorPool),
                    "CreateDescriptorPool");
            }
        }

        private unsafe void AllocateAndUpdateDescriptorSets()
{
    // Проверка на null / invalid
    if (_bufAnimEntries?.Handle.Handle == 0 ||
        _bufAnimIndex?.Handle.Handle == 0 ||
        _bufMorphDesc?.Handle.Handle == 0 ||
        _bufGeometry?.Handle.Handle == 0 ||
        _bufRenderInstances?.Handle.Handle == 0 ||
        _bufUniforms?.Handle.Handle == 0)
    {
        DebugManager.Warn("Cannot update descriptors — one or more buffers are null or invalid");
        return;
    }

    // Сначала выделяем дескриптор сет - используем fixed для поля класса
    fixed (DescriptorSetLayout* pLayout = &_descriptorSetLayout)
    {
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = pLayout
        };

        VulkanContext.Check(
            _ctx.Vk.AllocateDescriptorSets(_ctx.Device, &allocInfo, out _descriptorSet),
            "AllocateDescriptorSets");
    }

    // Подготавливаем структуры DescriptorBufferInfo
    DescriptorBufferInfo biAnimEntries = new() 
    { 
        Buffer = _bufAnimEntries.Handle, 
        Offset = 0, 
        Range = _bufAnimEntries.Size 
    };
    
    DescriptorBufferInfo biAnimIndex = new() 
    { 
        Buffer = _bufAnimIndex.Handle, 
        Offset = 0, 
        Range = _bufAnimIndex.Size 
    };
    
    DescriptorBufferInfo biMorphDesc = new() 
    { 
        Buffer = _bufMorphDesc.Handle, 
        Offset = 0, 
        Range = _bufMorphDesc.Size 
    };
    
    DescriptorBufferInfo biGeometry = new() 
    { 
        Buffer = _bufGeometry.Handle, 
        Offset = 0, 
        Range = _bufGeometry.Size 
    };
    
    DescriptorBufferInfo biRenderInstances = new() 
    { 
        Buffer = _bufRenderInstances.Handle, 
        Offset = 0, 
        Range = _bufRenderInstances.Size 
    };
    
    DescriptorBufferInfo biUniforms = new() 
    { 
        Buffer = _bufUniforms.Handle, 
        Offset = 0, 
        Range = _bufUniforms.Size 
    };

    // Создаем массив WriteDescriptorSet в стеке
    var writes = stackalloc WriteDescriptorSet[6];

    writes[0] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = BINDING_ANIM_ENTRIES,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &biAnimEntries
    };

    writes[1] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = BINDING_ANIM_INDEX,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &biAnimIndex
    };

    writes[2] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = BINDING_MORPH_DESC,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &biMorphDesc
    };

    writes[3] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = BINDING_GEOMETRY,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &biGeometry
    };

    writes[4] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = BINDING_RENDER_INST,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &biRenderInstances
    };

    writes[5] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _descriptorSet,
        DstBinding = 5,   // uniform buffer binding
        DescriptorCount = 1,
        DescriptorType = DescriptorType.UniformBuffer,
        PBufferInfo = &biUniforms
    };

    _ctx.Vk.UpdateDescriptorSets(
        _ctx.Device,
        descriptorWriteCount: 6,
        pDescriptorWrites: writes,
        descriptorCopyCount: 0,
        pDescriptorCopies: null
    );

    DebugManager.Memory($"Descriptors updated OK (6 bindings)");
}

        private WriteDescriptorSet MakeStorageWrite(uint binding, VulkanBuffer buf)
        {
            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = buf.Handle,
                Offset = 0,
                Range  = buf.Size
            };
            return new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descriptorSet,
                DstBinding      = binding,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.StorageBuffer,
                PBufferInfo     = &bufInfo  // NOTE: адрес стека — OK т.к. UpdateDescriptorSets копирует сразу
            };
        }

        private WriteDescriptorSet MakeUniformWrite(uint binding, VulkanBuffer buf)
        {
            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = buf.Handle,
                Offset = 0,
                Range  = buf.Size
            };
            return new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descriptorSet,
                DstBinding      = binding,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.UniformBuffer,
                PBufferInfo     = &bufInfo
            };
        }

        // ── Compute Pipelines ─────────────────────────────────────────────────

        private void CreateComputePipelines()
        {
            // Push constants layout: PrimIndex (int) + Time (float)
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset     = 0,
                Size       = (uint)sizeof(FramePushConstants)
            };

            fixed (DescriptorSetLayout* pDsl = &_descriptorSetLayout)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType                  = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount         = 1,
                    PSetLayouts            = pDsl,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges    = &pushRange
                };
                VulkanContext.Check(
                    _ctx.Vk.CreatePipelineLayout(_ctx.Device, &layoutInfo, null, out _computeLayout),
                    "CreatePipelineLayout (compute)");
            }

            _animComputePipeline  = CreateComputePipeline("anim_compute.spv");
            _morphComputePipeline = CreateComputePipeline("morph_compute.spv");

            DebugManager.Scene("VulkanAnimationEngine: Compute pipelines созданы.");
        }

        private Pipeline CreateComputePipeline(string spvFileName)
        {
            var spvPath = Path.Combine(ShaderPath, spvFileName);

            if (!File.Exists(spvPath))
            {
                // Шейдер ещё не скомпилирован — возвращаем пустой пайплайн с предупреждением.
                // Это позволяет запустить движок без шейдеров и компилировать их постепенно.
                DebugManager.Warn(
                    $"SPIR-V не найден: {spvPath}\n" +
                    $"Запусти: glslangValidator -V {Path.GetFileNameWithoutExtension(spvFileName)}.glsl -o {spvFileName}");
                return default;
            }

            var code = File.ReadAllBytes(spvPath);

            ShaderModule shaderModule;
            fixed (byte* pCode = code)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode    = (uint*)pCode
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateShaderModule(_ctx.Device, &moduleInfo, null, out shaderModule),
                    $"CreateShaderModule ({spvFileName})");
            }

            byte* entryName = (byte*)Marshal.StringToHGlobalAnsi("main");
            var stageInfo = new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName  = entryName
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType  = StructureType.ComputePipelineCreateInfo,
                Stage  = stageInfo,
                Layout = _computeLayout
            };

            VulkanContext.Check(
                _ctx.Vk.CreateComputePipelines(_ctx.Device, default, 1, &pipelineInfo, null, out var pipeline),
                $"CreateComputePipeline ({spvFileName})");

            _ctx.Vk.DestroyShaderModule(_ctx.Device, shaderModule, null);
            Marshal.FreeHGlobal((IntPtr)entryName);

            return pipeline;
        }

        // ── Graphics Pipeline ─────────────────────────────────────────────────

        private void CreateGraphicsPipeline()
        {
            // PipelineLayout для graphics включает те же descriptor sets
            // Push constants для graphics: aspectRatio + primIndex
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit,
                Offset     = 0,
                Size       = (uint)sizeof(FramePushConstants)
            };

            fixed (DescriptorSetLayout* pDsl = &_descriptorSetLayout)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType                  = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount         = 1,
                    PSetLayouts            = pDsl,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges    = &pushRange
                };
                VulkanContext.Check(
                    _ctx.Vk.CreatePipelineLayout(_ctx.Device, &layoutInfo, null, out _graphicsLayout),
                    "CreatePipelineLayout (graphics)");
            }

            var vertPath = Path.Combine(ShaderPath, "render.vert.spv");
            var fragPath = Path.Combine(ShaderPath, "render.frag.spv");

            if (!File.Exists(vertPath) || !File.Exists(fragPath))
            {
                DebugManager.Warn(
                    "Graphics SPIR-V не найдены. Pipeline не создан.\n" +
                    "Запусти: glslangValidator -V render.vert -o render.vert.spv\n" +
                    "         glslangValidator -V render.frag -o render.frag.spv");
                return;
            }

            var vertModule = CreateShaderModule(File.ReadAllBytes(vertPath));
            var fragModule = CreateShaderModule(File.ReadAllBytes(fragPath));

            byte* main = (byte*)Marshal.StringToHGlobalAnsi("main");

            var stages = new PipelineShaderStageCreateInfo[]
            {
                new()
                {
                    SType  = StructureType.PipelineShaderStageCreateInfo,
                    Stage  = ShaderStageFlags.VertexBit,
                    Module = vertModule,
                    PName  = main
                },
                new()
                {
                    SType  = StructureType.PipelineShaderStageCreateInfo,
                    Stage  = ShaderStageFlags.FragmentBit,
                    Module = fragModule,
                    PName  = main
                }
            };

            // Vertex input: vec2 position (binding 0, location 0)
            // В Vulkan-версии вершины берутся из SSBO напрямую в vertex shader,
            // поэтому vertex input может быть пустым (gl_VertexIndex → SSBO lookup)
            var vertexInputState = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount   = 0,
                VertexAttributeDescriptionCount = 0
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType    = StructureType.PipelineInputAssemblyStateCreateInfo,
                // LINE_STRIP аналог GL_LINE_STRIP — основной режим для контуров
                Topology = PrimitiveTopology.LineStrip,
                PrimitiveRestartEnable = true   // 0xFFFF = NaN-separator аналог
            };

            // Viewport/Scissor — динамические (меняются при resize без пересоздания pipeline)
            var dynamicStates = new DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (DynamicState* pDyn = dynamicStates)
            {
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType             = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates    = pDyn
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType         = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount  = 1
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType       = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode    = CullModeFlags.None,      // рисуем с обеих сторон
                    FrontFace   = FrontFace.CounterClockwise,
                    LineWidth   = 1.0f
                };

                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType                = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                // Blending (аналог GL.BlendFunc(SrcAlpha, OneMinusSrcAlpha))
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable         = true,
                    SrcColorBlendFactor = BlendFactor.SrcAlpha,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp        = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.Zero,
                    AlphaBlendOp        = BlendOp.Add,
                    ColorWriteMask      = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                          ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };

                var blending = new PipelineColorBlendStateCreateInfo
                {
                    SType           = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable   = false,
                    AttachmentCount = 1,
                    PAttachments    = &blendAttachment
                };

                fixed (PipelineShaderStageCreateInfo* pStages = stages)
                {
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType               = StructureType.GraphicsPipelineCreateInfo,
                        StageCount          = (uint)stages.Length,
                        PStages             = pStages,
                        PVertexInputState   = &vertexInputState,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState      = &viewportState,
                        PRasterizationState = &rasterizer,
                        PMultisampleState   = &multisampling,
                        PColorBlendState    = &blending,
                        PDynamicState       = &dynamicState,
                        Layout              = _graphicsLayout,
                        RenderPass          = _ctx.RenderPass,
                        Subpass             = 0
                    };

                    VulkanContext.Check(
                        _ctx.Vk.CreateGraphicsPipelines(_ctx.Device, default, 1, &pipelineInfo, null, out _graphicsPipeline),
                        "CreateGraphicsPipeline");
                }
            }

            _ctx.Vk.DestroyShaderModule(_ctx.Device, vertModule, null);
            _ctx.Vk.DestroyShaderModule(_ctx.Device, fragModule, null);
            Marshal.FreeHGlobal((IntPtr)main);

            DebugManager.Scene("VulkanAnimationEngine: Graphics pipeline создан.");
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* pCode = code)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode    = (uint*)pCode
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateShaderModule(_ctx.Device, &info, null, out var module),
                    "CreateShaderModule");
                return module;
            }
        }

        // ── Geometry upload (аналог AnimationEngine.UploadGeometryFromPrimitives) ──

        public void UploadGeometryFromPrimitives()
        {
            int totalVerts = _arena.TotalVertexCount;
            if (totalVerts <= 0) return;

            var allData = new float[totalVerts * 2];

            foreach (var p in _primitives)
            {
                if (p.VertexOffsetRaw < 0 || p.VertexCount <= 0) continue;
                var cached = p.GetVertices();
                if (cached is { Length: > 0 })
                {
                    for (int k = 0; k < cached.Length && k < p.VertexCount; k++)
                    {
                        int idx = (p.VertexOffsetRaw + k) * 2;
                        allData[idx + 0] = cached[k].X;
                        allData[idx + 1] = cached[k].Y;
                    }
                }
            }

            // Увеличиваем буфер если нужно
            ulong required = (ulong)(allData.Length * sizeof(float));
            if (required > _bufGeometry.Size)
            {
                _bufGeometry.Dispose();
                _bufGeometry = _vma.CreateVertexBuffer(required * 2); // с запасом x2
                // Обновляем descriptor set после пересоздания буфера
                UpdateGeometryDescriptor();
            }

            _vma.Upload(_bufGeometry, allData);

            DebugManager.Memory($"VulkanAnimationEngine: Загружено {totalVerts} вершин в geometry buffer.");
        }

        private void UpdateGeometryDescriptor()
        {
            var bufInfo = new DescriptorBufferInfo { Buffer = _bufGeometry.Handle, Offset = 0, Range = _bufGeometry.Size };

            var write = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descriptorSet,
                DstBinding      = BINDING_GEOMETRY,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.StorageBuffer,
                PBufferInfo     = &bufInfo
            };

            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
        }

        // ── Descriptor rebuild (аналог AnimationEngine.RebuildAllDescriptors) ──

        public void RebuildAllDescriptors()
        {
            InitMorphDescs();
            UploadMorphDescs();
            InitRenderInstances();
            UploadRenderInstances();
        }

        private void InitMorphDescs()
        {
            for (int i = 0; i < _primitives.Count; i++)
            {
                var p = _primitives[i];
                _morphDescs[i] = new MorphDescCpu
                {
                    OffsetA     = p.VertexOffsetA >= 0 ? p.VertexOffsetA : p.VertexOffsetRaw,
                    OffsetB     = p.VertexOffsetB >= 0 ? p.VertexOffsetB : p.VertexOffsetRaw,
                    OffsetM     = p.VertexOffsetM >= 0 ? p.VertexOffsetM : p.VertexOffsetRaw,
                    VertexCount = p.VertexCount
                };
            }
        }

        private void UploadMorphDescs() =>
            _vma.Upload(_bufMorphDesc, _morphDescs);

        private void InitRenderInstances()
        {
            for (int i = 0; i < _primitives.Count; i++)
                _renderInstances[i] = _primitives[i].ToRenderInstanceCpu();
        }

        private void UploadRenderInstances() =>
            _vma.Upload(_bufRenderInstances, _renderInstances);

        // ── Animation upload ──────────────────────────────────────────────────

        public void UploadPendingAnimationsAndIndex()
        {
            var newEntries = new List<AnimEntryCpu>();

            foreach (var prim in _primitives)
            {
                for (int i = 0; i < prim.PendingAnimations.Count; i++)
                {
                    var entry = prim.PendingAnimations[i];
                    if (!entry.PendingOnGpu) continue;

                    if (entry.PrimitiveId < 0 || entry.PrimitiveId >= _primitives.Count)
                        continue;

                    newEntries.Add(entry);
                    entry.PendingOnGpu = false;
                    prim.PendingAnimations[i] = entry;
                }
            }

            if (newEntries.Count == 0) return;

            _uploadedAnimEntries.AddRange(newEntries);

            // Загружаем все записи
            var bytes = PrimitiveGpu.SerializeAnimEntries(_uploadedAnimEntries);
            ulong required = (ulong)bytes.Length;

            if (required > _bufAnimEntries.Size)
            {
                _bufAnimEntries.Dispose();
                _bufAnimEntries = _vma.CreateStorageBuffer(required * 2);
                // Обновляем descriptor
                var bi = new DescriptorBufferInfo { Buffer = _bufAnimEntries.Handle, Offset = 0, Range = required * 2 };

                var wr = new WriteDescriptorSet
                {
                    SType           = StructureType.WriteDescriptorSet,
                    DstSet          = _descriptorSet,
                    DstBinding      = BINDING_ANIM_ENTRIES,
                    DescriptorCount = 1,
                    DescriptorType  = DescriptorType.StorageBuffer,
                    PBufferInfo     = &bi
                };

                _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &wr, 0, null);
            }

            _vma.Upload(_bufAnimEntries, bytes);

            // Обновляем индекс
            var index = PrimitiveGpu.BuildAnimIndex(_uploadedAnimEntries, _primitives.Count);
            _vma.Upload(_bufAnimIndex, index);
        }

        // ── Compute dispatch (аналог AnimationEngine.UpdateAndDispatch) ───────

        public void UpdateAndDispatch(float time)
        {
            if (_primitives.Count == 0) return;
            if (_uploadedAnimEntries.Count == 0) return;

            // Запускаем anim compute
            DispatchCompute(_animComputePipeline, time, -1, _primitives.Count);

            // Запускаем morph compute для примитивов с морфингом
            foreach (var p in _primitives)
            {
                if (p.VertexOffsetA < 0) continue;
                DispatchCompute(_morphComputePipeline, time, p.PrimitiveId, 1);
            }
        }

        private void DispatchCompute(Pipeline pipeline, float time, int primId, int groupCount)
        {
            if (pipeline.Handle == 0) return; // шейдер не загружен

            // Используем one-shot command buffer для compute
            // В production это стоит объединить в основной command buffer
            var cmd = _ctx.BeginSingleTimeCommands();

            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);

            fixed (DescriptorSet* pDs = &_descriptorSet)
                _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute,
                    _computeLayout, 0, 1, pDs, 0, null);

            var push = new FramePushConstants
            {
                Time        = time,
                AspectRatio = AspectRatio,
                PrimIndex   = primId
            };
            _ctx.Vk.CmdPushConstants(cmd, _computeLayout,
                ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &push);

            _ctx.Vk.CmdDispatch(cmd, (uint)Math.Max(1, groupCount), 1, 1);

            _ctx.EndSingleTimeCommands(cmd);
        }

        // ── DynOverrides (идентично AnimationEngine) ──────────────────────────

        public void ApplyDynOverrides(List<DynOverride> overrides)
        {
            foreach (var ov in overrides)
            {
                if (ov.Pid < 0 || ov.Pid >= _renderInstances.Length) continue;
                ref var inst = ref _renderInstances[ov.Pid];

                if (ov.HasPos)
                {
                    inst.TransformRow2 = new Vector4(ov.PosX, ov.PosY,
                        inst.TransformRow2.Z, inst.TransformRow2.W);
                }
                if (ov.HasRot)
                {
                    float c = MathF.Cos(ov.Rotation), s = MathF.Sin(ov.Rotation);
                    float sc = inst.TransformRow0.X / (MathF.Abs(inst.TransformRow0.X) < 1e-6f ? 1f : 1f); // сохраняем scale
                    inst.TransformRow0 = new Vector4(sc * c,  sc * s, 0, 0);
                    inst.TransformRow1 = new Vector4(-sc * s, sc * c, 0, 0);
                }
                if (ov.HasScale)
                {
                    float c = MathF.Cos(ov.Rotation);
                    float s_val = MathF.Sin(ov.Rotation);
                    inst.TransformRow0 = new Vector4(ov.Scale * c,   ov.Scale * s_val, 0, 0);
                    inst.TransformRow1 = new Vector4(-ov.Scale * s_val, ov.Scale * c,  0, 0);
                }
                if (ov.HasColor)
                    inst.Color = ov.Color;
            }

            UploadRenderInstances();
        }

        // ── Render (вызывается из VulkanSceneGpu.RecordCommandBuffer) ─────────

        public void RenderAll(CommandBuffer cmd, int imageIndex)
        {
            if (_graphicsPipeline.Handle == 0) return;

            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);

            // Динамический viewport (уже установлен снаружи, но для ясности:)
            var viewport = new Viewport
            {
                X        = 0,
                Y        = 0,
                Width    = _ctx.SwapchainExtent.Width,
                Height   = _ctx.SwapchainExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = _ctx.SwapchainExtent
            };
            _ctx.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
            _ctx.Vk.CmdSetScissor(cmd, 0, 1, &scissor);

            fixed (DescriptorSet* pDs = &_descriptorSet)
                _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                    _graphicsLayout, 0, 1, pDs, 0, null);

            // Рисуем каждый примитив — один draw call на примитив
            // (аналог цикла for primIndex in primitives: GL.DrawArrays)
            for (int i = 0; i < _primitives.Count; i++)
            {
                var p = _primitives[i];
                if (p.VertexCount <= 0) continue;

                var push = new FramePushConstants
                {
                    AspectRatio = AspectRatio,
                    PrimIndex   = i,
                    Time        = 0f // не нужен в vertex shader
                };
                _ctx.Vk.CmdPushConstants(cmd, _graphicsLayout,
                    ShaderStageFlags.VertexBit, 0, (uint)sizeof(FramePushConstants), &push);

                // Вершины читаются напрямую из SSBO в vertex shader
                // по gl_VertexIndex + offset из RenderInstance
                _ctx.Vk.CmdDraw(cmd,
                    vertexCount: (uint)p.VertexCount,
                    instanceCount: 1,
                    firstVertex: 0,
                    firstInstance: 0);
            }
        }

        // ── Swapchain recreate notification ───────────────────────────────────

        public void NotifySwapchainRecreated(VulkanContext ctx)
        {
            // Graphics pipeline зависит от RenderPass — нужно пересоздать
            if (_graphicsPipeline.Handle != 0)
                ctx.Vk.DestroyPipeline(ctx.Device, _graphicsPipeline, null);

            CreateGraphicsPipeline();
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ctx.Vk.DeviceWaitIdle(_ctx.Device);

            if (_animComputePipeline.Handle  != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _animComputePipeline,  null);
            if (_morphComputePipeline.Handle != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _morphComputePipeline, null);
            if (_graphicsPipeline.Handle     != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _graphicsPipeline,     null);

            _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _computeLayout,  null);
            _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _graphicsLayout, null);

            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, _descriptorPool, null);
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _descriptorSetLayout, null);

            _bufAnimEntries?.Dispose();
            _bufAnimIndex?.Dispose();
            _bufMorphDesc?.Dispose();
            _bufGeometry?.Dispose();
            _bufRenderInstances?.Dispose();
            _bufUniforms?.Dispose();
        }
    }
}