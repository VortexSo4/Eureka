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
        private VulkanBuffer _bufIndex          = null!;  // uint16, primitive-restart index buffer
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
            _bufGeometry        = _vma.CreateVertexBuffer(256 * 1024);
            _bufIndex           = _vma.CreateIndexBuffer(256 * 1024);  // uint16 indices
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

        // ── Geometry + Index upload ───────────────────────────────────────────────
        // Both staging arrays reused every frame — no per-frame heap allocation.
        //
        // INDEX BUFFER LOGIC (important — two bugs to avoid):
        //
        // The vertex shader does:  vec2 in_pos = geom[inst.meta.x + gl_VertexIndex]
        // So gl_VertexIndex must be the LOCAL (0-based) position within the primitive.
        // CmdDrawIndexed adds vertexOffset to each index from the buffer → gl_VertexIndex.
        // We pass vertexOffset=0, so gl_VertexIndex = raw index value from the buffer.
        //
        // For vertices [v0, v1, NaN, v2, v3]:
        //   Emit indices: [0, 1, 0xFFFF, 3, 4]   ← absolute k position, NaN slot skipped
        //   NOT:          [0, 1, 0xFFFF, 2, 3]   ← WRONG: index 2 still points to NaN slot
        //
        // 0xFFFF = primitive restart: GPU ends current LineStrip, starts a new one.
        // The NaN geometry slot is simply never referenced by any index.
        private float[]  _geometryStagingBuf = [];
        private ushort[] _indexStagingBuf    = [];
        private int[]    _primIndexStart     = [];
        private int[]    _primIndexCount     = [];

        public void UploadGeometryFromPrimitives()
        {
            if (_disposed) return;
            int totalVerts = _arena.TotalVertexCount;
            if (totalVerts <= 0) return;

            int primCount   = _primitives.Count;
            int geomNeeded  = totalVerts * 2;
            int indexNeeded = totalVerts; // worst case: one index per vertex

            // Grow staging arrays only when necessary
            if (_geometryStagingBuf.Length < geomNeeded)
                _geometryStagingBuf = new float[geomNeeded];
            if (_indexStagingBuf.Length < indexNeeded)
                _indexStagingBuf = new ushort[indexNeeded];
            if (_primIndexStart.Length < primCount)
            {
                _primIndexStart = new int[primCount];
                _primIndexCount = new int[primCount];
            }

            int globalIdxCursor = 0;

            foreach (var p in _primitives)
            {
                int pid = p.PrimitiveId;

                if (p.VertexOffsetRaw < 0 || p.VertexCount <= 0)
                {
                    if (pid >= 0 && pid < primCount)
                    {
                        _primIndexStart[pid] = globalIdxCursor;
                        _primIndexCount[pid] = 0;
                    }
                    continue;
                }

                var cached  = p.GetVertices();
                int limit   = cached is { Length: > 0 } ? Math.Min(cached.Length, p.VertexCount) : 0;
                int primStart = globalIdxCursor;

                for (int k = 0; k < limit; k++)
                {
                    float vx = cached[k].X;
                    float vy = cached[k].Y;

                    // Always write geometry (NaN slots stay NaN — they're never indexed)
                    int geomIdx = (p.VertexOffsetRaw + k) * 2;
                    _geometryStagingBuf[geomIdx]     = vx;
                    _geometryStagingBuf[geomIdx + 1] = vy;

                    if (float.IsNaN(vx) || float.IsNaN(vy))
                    {
                        // Primitive restart — ends current LineStrip, starts next.
                        // The NaN slot at position k is never referenced by an index.
                        _indexStagingBuf[globalIdxCursor++] = 0xFFFF;
                    }
                    else
                    {
                        // Emit absolute local position k as the index.
                        // gl_VertexIndex = k (with vertexOffset=0 in CmdDrawIndexed).
                        // Shader reads geom[OffsetM + k] = correct vertex.
                        _indexStagingBuf[globalIdxCursor++] = (ushort)k;
                    }
                }

                if (pid >= 0 && pid < primCount)
                {
                    _primIndexStart[pid] = primStart;
                    _primIndexCount[pid] = globalIdxCursor - primStart;
                }
            }

            // ── Upload geometry ───────────────────────────────────────────────
            ulong geomRequired = (ulong)(geomNeeded * sizeof(float));
            if (geomRequired > _bufGeometry.Size)
            {
                _ctx.Vk.DeviceWaitIdle(_ctx.Device);
                _bufGeometry.Dispose();
                _bufGeometry = _vma.CreateVertexBuffer(geomRequired * 4);
                UpdateGeometryDescriptor();
            }
            _vma.Upload(_bufGeometry, new ReadOnlySpan<float>(_geometryStagingBuf, 0, geomNeeded));

            // ── Upload index buffer ───────────────────────────────────────────
            ulong idxRequired = (ulong)(globalIdxCursor * sizeof(ushort));
            if (idxRequired > _bufIndex.Size)
            {
                _ctx.Vk.DeviceWaitIdle(_ctx.Device);
                _bufIndex.Dispose();
                _bufIndex = _vma.CreateIndexBuffer(idxRequired * 4);
            }
            _vma.Upload(_bufIndex, new ReadOnlySpan<ushort>(_indexStagingBuf, 0, globalIdxCursor));

            DebugManager.Memory($"VulkanAnimationEngine: {totalVerts} вершин, {globalIdxCursor} индексов.");
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

        private void UploadMorphDescs()
        {
            if (_disposed) return;
            _vma.Upload(_bufMorphDesc, _morphDescs);
        }

        private void InitRenderInstances()
        {
            for (int i = 0; i < _primitives.Count; i++)
                _renderInstances[i] = _primitives[i].ToRenderInstanceCpu();
        }

        private void UploadRenderInstances()
        {
            if (_disposed) return;
            _vma.Upload(_bufRenderInstances, _renderInstances);
        }

        // ── Animation upload ──────────────────────────────────────────────────

        public void UploadPendingAnimationsAndIndex()
        {
            if (_disposed) return;
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
                // Wait for GPU before destroying the buffer it may still reference.
                _ctx.Vk.DeviceWaitIdle(_ctx.Device);
                _bufAnimEntries.Dispose();
                _bufAnimEntries = _vma.CreateStorageBuffer(required * 2);
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
        //
        // Все dispatches идут в ОДИН command buffer с барьерами между ними.
        // Старый вариант вызывал BeginSingleTimeCommands() → QueueWaitIdle на каждый
        // dispatch — полная сериализация GPU при наличии морф-примитивов.
        // Новый вариант: один submit → одно ожидание в конце.

        public void UpdateAndDispatch(float time)
        {
            if (_primitives.Count == 0) return;
            if (_uploadedAnimEntries.Count == 0) return;
            if (_animComputePipeline.Handle == 0) return;

            var cmd = _ctx.BeginSingleTimeCommands();

            // ── 1. Anim compute: один dispatch покрывает ВСЕ примитивы ──
            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _animComputePipeline);

            fixed (DescriptorSet* pDs = &_descriptorSet)
                _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute,
                    _computeLayout, 0, 1, pDs, 0, null);

            var animPush = new FramePushConstants
            {
                Time        = time,
                AspectRatio = AspectRatio,
                PrimIndex   = -1
            };
            _ctx.Vk.CmdPushConstants(cmd, _computeLayout,
                ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &animPush);

            // local_size_x = 64 в anim_compute.comp
            uint animGroups = (uint)Math.Max(1, (_primitives.Count + 63) / 64);
            _ctx.Vk.CmdDispatch(cmd, animGroups, 1, 1);

            // ── 2. Барьер: anim записал RenderInstances/MorphDescs → morph читает ──
            var ssboBarrier = new MemoryBarrier
            {
                SType         = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
            };
            _ctx.Vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit,
                DependencyFlags.None,
                1, &ssboBarrier,
                0, null,
                0, null);

            // ── 3. Morph compute: по одному dispatch на примитив с морф-таргетами ──
            if (_morphComputePipeline.Handle != 0)
            {
                bool pipelineBound = false;

                foreach (var p in _primitives)
                {
                    if (p.VertexOffsetA < 0) continue;

                    if (!pipelineBound)
                    {
                        _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _morphComputePipeline);

                        fixed (DescriptorSet* pDs = &_descriptorSet)
                            _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute,
                                _computeLayout, 0, 1, pDs, 0, null);

                        pipelineBound = true;
                    }

                    var morphPush = new FramePushConstants
                    {
                        Time        = time,
                        AspectRatio = AspectRatio,
                        PrimIndex   = p.PrimitiveId
                    };
                    _ctx.Vk.CmdPushConstants(cmd, _computeLayout,
                        ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &morphPush);

                    // local_size_x = 256 в morph_compute.comp
                    uint morphGroups = (uint)Math.Max(1u, ((uint)p.VertexCount + 255) / 256);
                    _ctx.Vk.CmdDispatch(cmd, morphGroups, 1, 1);
                }
            }

            // ── 4. Финальный барьер: compute записал геометрию → vertex shader читает ──
            var vsBarrier = new MemoryBarrier
            {
                SType         = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            };
            _ctx.Vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit,
                DependencyFlags.None,
                1, &vsBarrier,
                0, null,
                0, null);

            _ctx.EndSingleTimeCommands(cmd); // ← одно QueueWaitIdle вместо N
        }

        // ── DynOverrides (идентично AnimationEngine) ──────────────────────────

        public void ApplyDynOverrides(List<DynOverride> overrides)
        {
            if (_disposed) return;
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
            if (_disposed) return;
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

            // Bind index buffer once for all primitives.
            // vertexOffset=0 in CmdDrawIndexed: gl_VertexIndex = raw index from buffer (0,1,2,...).
            // Shader handles global arena offset via inst.meta.x (OffsetM).
            _ctx.Vk.CmdBindIndexBuffer(cmd, _bufIndex.Handle, 0, IndexType.Uint16);

            for (int i = 0; i < _primitives.Count; i++)
            {
                var p  = _primitives[i];
                int pid = p.PrimitiveId;
                if (p.VertexCount <= 0) continue;

                int idxCount = (pid >= 0 && pid < _primIndexCount.Length) ? _primIndexCount[pid] : 0;
                int idxStart = (pid >= 0 && pid < _primIndexStart.Length) ? _primIndexStart[pid] : 0;
                if (idxCount <= 0) continue;

                var push = new FramePushConstants
                {
                    AspectRatio = AspectRatio,
                    PrimIndex   = i,
                    Time        = 0f
                };
                _ctx.Vk.CmdPushConstants(cmd, _graphicsLayout,
                    ShaderStageFlags.VertexBit, 0, (uint)sizeof(FramePushConstants), &push);

                _ctx.Vk.CmdDrawIndexed(cmd,
                    indexCount:    (uint)idxCount,
                    instanceCount: 1,
                    firstIndex:    (uint)idxStart,
                    vertexOffset:  0,  // DO NOT pass VertexOffsetRaw — shader adds OffsetM itself
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

            _ctx.Vk.DeviceWaitIdle(_ctx.Device);   // ← можно оставить здесь, на всякий случай

            if (_computeLayout.Handle != 0)
                _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _computeLayout, null);

            if (_graphicsLayout.Handle != 0)
                _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _graphicsLayout, null);

            // Потом pipelines
            if (_animComputePipeline.Handle  != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _animComputePipeline,  null);
            if (_morphComputePipeline.Handle != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _morphComputePipeline, null);
            if (_graphicsPipeline.Handle     != 0) _ctx.Vk.DestroyPipeline(_ctx.Device, _graphicsPipeline,     null);

            // Остальное без изменений
            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, _descriptorPool, null);
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _descriptorSetLayout, null);

            _bufAnimEntries?.Dispose();
            _bufAnimIndex?.Dispose();
            _bufMorphDesc?.Dispose();
            _bufGeometry?.Dispose();
            _bufIndex?.Dispose();
            _bufRenderInstances?.Dispose();
            _bufUniforms?.Dispose();
        }
    }
}