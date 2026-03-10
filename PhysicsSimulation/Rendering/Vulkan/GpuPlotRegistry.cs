// ============================================================
//  GpuPlotRegistry.cs
//  EurekaSharp — GPU Plot Evaluation
//
//  Управляет коллекцией PlotGpu примитивов которые вычисляются
//  на GPU (plot_compute.comp) вместо CPU.
//
//  Жизненный цикл:
//    1. RegisterPlot(plot, program) — при AddPrimitive
//    2. UploadPlotParams(frame, T, MX, MY) — в FlushPendingUploads
//    3. RecordPlotDispatch(cmd, frame) — в RecordComputeCommands
//       (до anim compute, с барьером после)
//
//  CPU больше не вызывает RefreshDynamicVertices() для GPU-plots.
// ============================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using Silk.NET.Vulkan;

namespace PhysicsSimulation.Rendering.Vulkan
{
    public sealed unsafe class GpuPlotRegistry : IDisposable
    {
        private readonly VulkanContext         _ctx;
        private readonly VulkanMemoryAllocator _vma;

        // Один PlotEntry на каждый PlotGpu зарегистрированный для GPU-вычисления
        private sealed class PlotEntry
        {
            public PlotGpu             Plot;
            public PlotBytecodeProgram Program;
            public int                 GpuSlot;   // индекс в _plotParams[]
        }

        private readonly List<PlotEntry> _entries = new();

        // Per-frame PlotParams буферы (как остальные per-frame в AnimationEngine)
        private const int MaxFrames = VulkanContext.MaxFramesInFlight;
        private readonly VulkanBuffer[] _bufPlotParams = new VulkanBuffer[MaxFrames];

        // CPU-side staging array, переиспользуется
        private PlotParamsGpu[] _stagingParams = Array.Empty<PlotParamsGpu>();

        // Pipeline
        private Pipeline       _plotComputePipeline;
        private PipelineLayout _plotComputeLayout;

        // Descriptor set layout и sets — shared с AnimationEngine через биндинги
        // binding 3 = GeometryArena (write), binding 6 = PlotParams (read)
        private DescriptorSetLayout   _descSetLayout;
        private DescriptorPool        _descPool;
        private readonly DescriptorSet[] _descSets = new DescriptorSet[MaxFrames];

        // Ссылка на shared геометрию (binding 3 — owned by AnimationEngine)
        private VulkanBuffer _sharedGeometryBuf;

        private bool _dirty     = true;   // нужно ли перезаливать _bufPlotParams
        private bool _disposed;

        public int PlotCount => _entries.Count;

        public GpuPlotRegistry(
            VulkanContext ctx,
            VulkanMemoryAllocator vma,
            VulkanBuffer sharedGeometryBuf)
        {
            _ctx              = ctx;
            _vma              = vma;
            _sharedGeometryBuf = sharedGeometryBuf;

            AllocateParamBuffers();
            CreateDescriptorLayout();
            CreateDescriptorPool();
            AllocateDescriptorSets();
            CreatePipeline();
        }

        // ── Buffer allocation ─────────────────────────────────────────────────

        private void AllocateParamBuffers()
        {
            // Начальный размер: 16 plots. Вырастет при необходимости.
            ulong initialSize = (ulong)(16 * sizeof(PlotParamsGpu));
            for (int f = 0; f < MaxFrames; f++)
            {
                _bufPlotParams[f] = _vma.CreateStorageBuffer(initialSize);
                _bufPlotParams[f].EnablePersistentMap();
            }
        }

        // ── Descriptor layout: binding 3 (geometry, readonly) + binding 6 (plot params) ──

        private void CreateDescriptorLayout()
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding         = 3,
                DescriptorType  = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags      = ShaderStageFlags.ComputeBit
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding         = 6,
                DescriptorType  = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags      = ShaderStageFlags.ComputeBit
            };

            var info = new DescriptorSetLayoutCreateInfo
            {
                SType        = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings    = bindings
            };
            VulkanContext.Check(
                _ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, &info, null, out _descSetLayout),
                "GpuPlotRegistry.CreateDescriptorSetLayout");
        }

        private void CreateDescriptorPool()
        {
            var sizes = stackalloc DescriptorPoolSize[1];
            sizes[0] = new DescriptorPoolSize
            {
                Type             = DescriptorType.StorageBuffer,
                DescriptorCount  = 2u * MaxFrames
            };

            var info = new DescriptorPoolCreateInfo
            {
                SType         = StructureType.DescriptorPoolCreateInfo,
                MaxSets       = (uint)MaxFrames,
                PoolSizeCount = 1,
                PPoolSizes    = sizes
            };
            VulkanContext.Check(
                _ctx.Vk.CreateDescriptorPool(_ctx.Device, &info, null, out _descPool),
                "GpuPlotRegistry.CreateDescriptorPool");
        }

        private void AllocateDescriptorSets()
        {
            var layouts = stackalloc DescriptorSetLayout[MaxFrames];
            for (int f = 0; f < MaxFrames; f++) layouts[f] = _descSetLayout;

            fixed (DescriptorSet* pSets = _descSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType              = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool     = _descPool,
                    DescriptorSetCount = (uint)MaxFrames,
                    PSetLayouts        = layouts
                };
                VulkanContext.Check(
                    _ctx.Vk.AllocateDescriptorSets(_ctx.Device, &allocInfo, pSets),
                    "GpuPlotRegistry.AllocateDescriptorSets");
            }

            for (int f = 0; f < MaxFrames; f++)
                WriteDescriptors(f);
        }

        private void WriteDescriptors(int frame)
        {
            var biGeom  = new DescriptorBufferInfo { Buffer = _sharedGeometryBuf.Handle, Offset = 0, Range = _sharedGeometryBuf.Size };
            var biPlot  = new DescriptorBufferInfo { Buffer = _bufPlotParams[frame].Handle, Offset = 0, Range = _bufPlotParams[frame].Size };

            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descSets[frame],
                DstBinding      = 3,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.StorageBuffer,
                PBufferInfo     = &biGeom
            };
            writes[1] = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _descSets[frame],
                DstBinding      = 6,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.StorageBuffer,
                PBufferInfo     = &biPlot
            };
            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 2, writes, 0, null);
        }

        // ── Pipeline ──────────────────────────────────────────────────────────

        private void CreatePipeline()
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset     = 0,
                Size       = (uint)sizeof(FramePushConstants)
            };

            fixed (DescriptorSetLayout* pDsl = &_descSetLayout)
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
                    _ctx.Vk.CreatePipelineLayout(_ctx.Device, &layoutInfo, null, out _plotComputeLayout),
                    "GpuPlotRegistry.CreatePipelineLayout");
            }

            var spvPath = System.IO.Path.Combine(VulkanAnimationEngine.ShaderPath, "plot_compute.spv");
            if (!System.IO.File.Exists(spvPath))
            {
                DebugManager.Warn($"[GpuPlotRegistry] plot_compute.spv не найден: {spvPath}. GPU plots отключены.");
                return;
            }

            var code = System.IO.File.ReadAllBytes(spvPath);
            byte* entryName = (byte*)Marshal.StringToHGlobalAnsi("main");

            fixed (byte* pCode = code)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode    = (uint*)pCode
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateShaderModule(_ctx.Device, &moduleInfo, null, out var shaderModule),
                    "GpuPlotRegistry.CreateShaderModule");

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
                    Layout = _plotComputeLayout
                };
                VulkanContext.Check(
                    _ctx.Vk.CreateComputePipelines(_ctx.Device, default, 1, &pipelineInfo, null, out _plotComputePipeline),
                    "GpuPlotRegistry.CreateComputePipeline");

                _ctx.Vk.DestroyShaderModule(_ctx.Device, shaderModule, null);
            }

            Marshal.FreeHGlobal((IntPtr)entryName);
            DebugManager.Scene("[GpuPlotRegistry] plot_compute pipeline создан.");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Регистрирует PlotGpu для GPU-вычисления.
        /// Вызывается из VulkanAnimationEngine после регистрации геометрии.
        /// Возвращает true если компиляция bytecode прошла успешно.
        /// </summary>
        public bool RegisterPlot(PlotGpu plot, LambdaExpr? lambdaAst)
        {
            if (lambdaAst == null) return false;

            var prog = PlotBytecodeCompiler.Compile(lambdaAst);
            if (!prog.IsValid)
            {
                DebugManager.Warn($"[GpuPlotRegistry] Bytecode compile failed for '{plot.Name}': {prog.CompileError}. " +
                                  $"Plot will use CPU fallback.");
                return false;
            }

            int slot = _entries.Count;
            _entries.Add(new PlotEntry { Plot = plot, Program = prog, GpuSlot = slot });
            _dirty = true;

            // Помечаем PlotGpu что вычисление делает GPU — отключаем CPU path
            plot.GpuComputed = true;

            DebugManager.Scene($"[GpuPlotRegistry] '{plot.Name}' → GPU slot {slot}, " +
                               $"{prog.Instructions.Count} instrs, {prog.Constants.Count} consts, " +
                               $"snaps: [{string.Join(", ", prog.SnapNames)}]");
            return true;
        }

        /// <summary>
        /// Обновляет geometry binding (при росте буфера геометрии в AnimationEngine).
        /// </summary>
        public void NotifyGeometryBufferReplaced(VulkanBuffer newGeometryBuf)
        {
            _sharedGeometryBuf = newGeometryBuf;
            for (int f = 0; f < MaxFrames; f++)
                WriteDescriptors(f);
        }

        /// <summary>
        /// Заливает PlotParams на GPU для текущего frame slot.
        /// Вызывать из FlushPendingUploads после WaitForFences.
        /// </summary>
        public void UploadPlotParams(int frame, float t, float mx, float my)
        {
            if (_entries.Count == 0) return;

            // Grow staging if needed
            if (_stagingParams.Length < _entries.Count)
                _stagingParams = new PlotParamsGpu[_entries.Count * 2];

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var plot  = entry.Plot;
                var prog  = entry.Program;
                ref var p = ref _stagingParams[i];

                p.XMin         = plot.XMin;
                p.XMax         = plot.XMax;
                p.VertexOffset = plot.VertexOffsetRaw;
                p.Resolution   = plot.Resolution + 1;  // Resolution+1 точек

                // Snapshot переменные
                p.T   = t;
                p.MX  = mx;
                p.MY  = my;
                p.Reserved0 = 0f;

                // Дополнительные snapshot переменные (индексы 3+ в prog.SnapNames)
                p.SnapA = System.Numerics.Vector4.Zero;
                p.SnapB = System.Numerics.Vector4.Zero;
                for (int s = 3; s < prog.SnapNames.Count && s < PlotBytecodeProgram.MaxSnaps; s++)
                {
                    float snapVal = 0f;
                    if (ESharpEngine.Registry.TryGetVar(prog.SnapNames[s], out var snapObj))
                        snapVal = (float)Convert.ToDouble(snapObj);

                    int localIdx = s - 3;
                    if (localIdx < 4)
                    {
                        // SnapA slots 0-3
                        switch (localIdx)
                        {
                            case 0: p.SnapA.X = snapVal; break;
                            case 1: p.SnapA.Y = snapVal; break;
                            case 2: p.SnapA.Z = snapVal; break;
                            case 3: p.SnapA.W = snapVal; break;
                        }
                    }
                    else
                    {
                        // SnapB slots 0-3
                        int bi = localIdx - 4;
                        switch (bi)
                        {
                            case 0: p.SnapB.X = snapVal; break;
                            case 1: p.SnapB.Y = snapVal; break;
                            case 2: p.SnapB.Z = snapVal; break;
                            case 3: p.SnapB.W = snapVal; break;
                        }
                    }
                }

                p.SetBytecode(prog);
            }

            // Grow GPU buffer if needed
            ulong required = (ulong)(_entries.Count * sizeof(PlotParamsGpu));
            if (required > _bufPlotParams[frame].Size)
            {
                _bufPlotParams[frame].Dispose();
                _bufPlotParams[frame] = _vma.CreateStorageBuffer(required * 2);
                _bufPlotParams[frame].EnablePersistentMap();
                WriteDescriptors(frame);
            }

            _bufPlotParams[frame].Write(
                new ReadOnlySpan<PlotParamsGpu>(_stagingParams, 0, _entries.Count));
        }

        /// <summary>
        /// Записывает dispatch команды в command buffer.
        /// Один workgroup (local_size_x=256) на каждый plot.
        /// Вызывать ДО anim_compute, с барьером после.
        /// </summary>
        public void RecordDispatch(CommandBuffer cmd, int frame, float time, float aspectRatio)
        {
            if (_entries.Count == 0) return;
            if (_plotComputePipeline.Handle == 0) return;

            var ds = _descSets[frame];
            _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _plotComputePipeline);
            _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute,
                _plotComputeLayout, 0, 1, &ds, 0, null);

            var push = new FramePushConstants
            {
                Time        = time,
                AspectRatio = aspectRatio,
                PrimIndex   = -1
            };
            _ctx.Vk.CmdPushConstants(cmd, _plotComputeLayout,
                ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FramePushConstants), &push);

            // Один workgroup на каждый plot — каждый workgroup вычисляет все точки своего графика
            // (gl_WorkGroupID.x = plotIdx, gl_LocalInvocationID.x = pointIdx, local_size_x = 256)
            _ctx.Vk.CmdDispatch(cmd, (uint)_entries.Count, 1, 1);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_plotComputePipeline.Handle != 0)
                _ctx.Vk.DestroyPipeline(_ctx.Device, _plotComputePipeline, null);
            if (_plotComputeLayout.Handle != 0)
                _ctx.Vk.DestroyPipelineLayout(_ctx.Device, _plotComputeLayout, null);

            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, _descPool, null);
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _descSetLayout, null);

            for (int f = 0; f < MaxFrames; f++)
                _bufPlotParams[f]?.Dispose();
        }
    }
}