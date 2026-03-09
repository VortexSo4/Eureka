// ============================================================
//  VulkanMemory.cs
//  EurekaSharp — Vulkan Backend
//
//  Заменяет всё что было в AnimationEngine через GL.GenBuffer +
//  GL.BufferData + GL.BufferSubData.
//
//  Концепция:
//    VulkanBuffer — один аллоцированный VkBuffer с памятью.
//    VulkanMemoryAllocator — создаёт буферы нужного типа.
//
//  Типы буферов (соответствие старому коду):
//    ─ StorageBuffer (SSBO) → VkBufferUsage.StorageBufferBit
//    ─ VertexBuffer          → VkBufferUsage.VertexBufferBit
//    ─ StagingBuffer         → host-visible, для CPU→GPU копирования
//
//  Стратегия памяти для Android:
//    На мобильниках GPU и CPU часто используют единую память (UMA),
//    поэтому HOST_VISIBLE | DEVICE_LOCAL часто доступны одновременно.
//    Мы проверяем это и по возможности пропускаем staging.
//
// ============================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace PhysicsSimulation.Rendering.Vulkan
{
    /// <summary>
    /// Пара VkBuffer + VkDeviceMemory с удобным API для записи данных.
    /// </summary>
    public sealed unsafe class VulkanBuffer : IDisposable
    {
        private readonly VulkanContext _ctx;
        private bool _disposed;

        public Silk.NET.Vulkan.Buffer     Handle     { get; private set; }
        public DeviceMemory Memory   { get; private set; }
        public ulong      Size       { get; private set; }
        public bool       IsHostVisible { get; private set; }

        internal VulkanBuffer(VulkanContext ctx, Silk.NET.Vulkan.Buffer handle, DeviceMemory memory,
                              ulong size, bool hostVisible)
        {
            _ctx          = ctx;
            Handle        = handle;
            Memory        = memory;
            Size          = size;
            IsHostVisible = hostVisible;
        }

        // ── Запись данных (host-visible буфер) ───────────────────────────────

        /// <summary>
        /// Копирует managed-массив структур в буфер.
        /// Буфер должен быть host-visible (staging или UMA device-local+host-visible).
        /// Аналог GL.BufferSubData.
        /// </summary>
        public void Write<T>(T[] data, ulong offsetBytes = 0) where T : unmanaged
        {
            if (!IsHostVisible)
                throw new InvalidOperationException("Нельзя писать напрямую — буфер не host-visible. Используй staging.");

            if (data == null || data.Length == 0) return; // пустой массив — ничего не делаем

            ulong dataSize = (ulong)(data.Length * sizeof(T));
            if (dataSize + offsetBytes > Size)
                throw new ArgumentOutOfRangeException(nameof(data),
                    $"Данные ({dataSize} байт) выходят за размер буфера ({Size} байт).");

            void* mapped;
            VulkanContext.Check(
                _ctx.Vk.MapMemory(_ctx.Device, Memory, offsetBytes, dataSize, 0, &mapped),
                "MapMemory");

            fixed (T* src = data)
                Unsafe.CopyBlock(mapped, src, (uint)dataSize);

            _ctx.Vk.UnmapMemory(_ctx.Device, Memory);
        }

        /// <summary>
        /// Span-версия Write — zero-copy для стек-аллоцированных данных.
        /// </summary>
        public void Write<T>(ReadOnlySpan<T> data, ulong offsetBytes = 0) where T : unmanaged
        {
            if (!IsHostVisible)
                throw new InvalidOperationException("Буфер не host-visible.");

            if (data.IsEmpty) return; // пустой span — ничего не делаем

            ulong dataSize = (ulong)(data.Length * sizeof(T));

            void* mapped;
            VulkanContext.Check(
                _ctx.Vk.MapMemory(_ctx.Device, Memory, offsetBytes, dataSize, 0, &mapped),
                "MapMemory");

            fixed (T* src = data)
                Unsafe.CopyBlock(mapped, src, (uint)dataSize);

            _ctx.Vk.UnmapMemory(_ctx.Device, Memory);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ctx.Vk.DestroyBuffer(_ctx.Device, Handle, null);
            _ctx.Vk.FreeMemory(_ctx.Device, Memory, null);
        }
    }

    /// <summary>
    /// Фабрика для создания VulkanBuffer-ов.
    /// Аналог GL.GenBuffer — но с явным контролем памяти.
    /// </summary>
    public sealed unsafe class VulkanMemoryAllocator : IDisposable
    {
        private readonly VulkanContext _ctx;

        // Кэш: есть ли память HOST_VISIBLE | DEVICE_LOCAL (UMA, типично для Android)
        private readonly bool _hasUmaMemory;

        public VulkanMemoryAllocator(VulkanContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _hasUmaMemory = DetectUmaMemory();

            if (_hasUmaMemory)
                Console.WriteLine("[VMA] UMA память обнаружена — staging буферы не нужны.");
        }

        // ── Public factory methods ───────────────────────────────────────────

        /// <summary>
        /// SSBO (Storage Buffer) — аналог GL ShaderStorageBuffer.
        /// На UMA железе (Android) — HOST_VISIBLE|DEVICE_LOCAL, staging не нужен.
        /// На дискретном GPU — DEVICE_LOCAL, нужен staging для upload.
        /// </summary>
        public VulkanBuffer CreateStorageBuffer(ulong sizeBytes, bool dynamic = true)
        {
            var usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;

            if (_hasUmaMemory || dynamic)
            {
                // Прямая запись с CPU — хорошо для часто обновляемых буферов (анимации)
                return AllocateBuffer(sizeBytes, usage,
                    MemoryPropertyFlags.HostVisibleBit |
                    MemoryPropertyFlags.HostCoherentBit);
            }
            else
            {
                // Статическая геометрия на дискретном GPU — только DEVICE_LOCAL
                return AllocateBuffer(sizeBytes, usage,
                    MemoryPropertyFlags.DeviceLocalBit,
                    hostVisible: false);
            }
        }

        /// <summary>
        /// Vertex buffer — аналог GL ArrayBuffer.
        /// Для геометрии ArenaBuffer который читает vertex shader.
        /// </summary>
        public VulkanBuffer CreateVertexBuffer(ulong sizeBytes)
        {
            var usage = BufferUsageFlags.VertexBufferBit  |
                        BufferUsageFlags.StorageBufferBit | // нужен для compute (морфинг)
                        BufferUsageFlags.TransferDstBit;

            if (_hasUmaMemory)
            {
                // UMA (iGPU): HostVisible|DeviceLocal — прямая запись без копирования
                return AllocateBuffer(sizeBytes, usage,
                    MemoryPropertyFlags.HostVisibleBit |
                    MemoryPropertyFlags.HostCoherentBit |
                    MemoryPropertyFlags.DeviceLocalBit);
            }

            // Дискретный GPU: геометрия обновляется каждый кадр (dynamic primitives),
            // поэтому используем HostVisible — Map/Write/Unmap каждый кадр.
            // DeviceLocal-only + staging здесь избыточен: staging сам создаёт
            // дополнительный HostVisible буфер, что медленнее чем прямой map.
            return AllocateBuffer(sizeBytes, usage,
                MemoryPropertyFlags.HostVisibleBit |
                MemoryPropertyFlags.HostCoherentBit);
        }

        /// <summary>
        /// Index buffer (uint16) — для vkCmdDrawIndexed.
        /// 0xFFFF = primitive restart token (разрыв LineStrip между контурами).
        /// Обновляется каждый кадр вместе с геометрией → HostVisible.
        /// </summary>
        public VulkanBuffer CreateIndexBuffer(ulong sizeBytes)
        {
            var usage = BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit;

            if (_hasUmaMemory)
                return AllocateBuffer(sizeBytes, usage,
                    MemoryPropertyFlags.HostVisibleBit |
                    MemoryPropertyFlags.HostCoherentBit |
                    MemoryPropertyFlags.DeviceLocalBit);

            return AllocateBuffer(sizeBytes, usage,
                MemoryPropertyFlags.HostVisibleBit |
                MemoryPropertyFlags.HostCoherentBit);
        }

        /// <summary>
        /// Staging buffer — временный host-visible буфер для копирования данных на GPU.
        /// Используется только на дискретных GPU (не UMA).
        /// Аналог "CPU-side copy buffer" перед GL.BufferData.
        /// </summary>
        public VulkanBuffer CreateStagingBuffer(ulong sizeBytes)
        {
            return AllocateBuffer(sizeBytes,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }

        /// <summary>
        /// Uniform buffer — маленькие часто-меняемые данные (aspect ratio, time и т.д.)
        /// Аналог GL.Uniform* — но здесь данные в буфере.
        /// </summary>
        public VulkanBuffer CreateUniformBuffer(ulong sizeBytes)
        {
            return AllocateBuffer(sizeBytes,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }

        // ── Утилита: копирование через staging на device-local буфер ─────────

        /// <summary>
        /// Заливает данные в device-local буфер через staging.
        /// Используй для статической геометрии на дискретных GPU.
        /// </summary>
        public void UploadViaStagingBuffer<T>(VulkanBuffer dest, T[] data) where T : unmanaged
        {
            ulong size = (ulong)(data.Length * sizeof(T));
            using var staging = CreateStagingBuffer(size);
            staging.Write(data);
            CopyBuffer(staging, dest, size);
        }

        public void UploadViaStagingBuffer<T>(VulkanBuffer dest, ReadOnlySpan<T> data) where T : unmanaged
        {
            ulong size = (ulong)(data.Length * sizeof(T));
            using var staging = CreateStagingBuffer(size);
            staging.Write(data);
            CopyBuffer(staging, dest, size);
        }

        // ── Умный upload: сам выбирает staging или прямую запись ─────────────

        /// <summary>
        /// Универсальный upload — прямая запись если host-visible, staging если нет.
        /// Используй вместо GL.BufferSubData.
        /// </summary>
        public void Upload<T>(VulkanBuffer dest, T[] data, ulong offsetBytes = 0) where T : unmanaged
        {
            if (dest.IsHostVisible)
                dest.Write(data, offsetBytes);
            else
                UploadViaStagingBuffer(dest, data); // игнорирует offset для простоты
        }

        public void Upload<T>(VulkanBuffer dest, ReadOnlySpan<T> data, ulong offsetBytes = 0) where T : unmanaged
        {
            if (dest.IsHostVisible)
                dest.Write(data, offsetBytes);
            else
                UploadViaStagingBuffer(dest, data);
        }

        // ── Внутренние методы ────────────────────────────────────────────────

        private VulkanBuffer AllocateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memProps,
            bool hostVisible = true)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType       = StructureType.BufferCreateInfo,
                Size        = size,
                Usage       = usage,
                SharingMode = SharingMode.Exclusive
            };

            VulkanContext.Check(
                _ctx.Vk.CreateBuffer(_ctx.Device, &bufferInfo, null, out var buffer),
                "CreateBuffer");

            _ctx.Vk.GetBufferMemoryRequirements(_ctx.Device, buffer, out var memReqs);

            uint memType = FindMemoryType(memReqs.MemoryTypeBits, memProps);

            var allocInfo = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                AllocationSize  = memReqs.Size,
                MemoryTypeIndex = memType
            };

            VulkanContext.Check(
                _ctx.Vk.AllocateMemory(_ctx.Device, &allocInfo, null, out var memory),
                "AllocateMemory");

            VulkanContext.Check(
                _ctx.Vk.BindBufferMemory(_ctx.Device, buffer, memory, 0),
                "BindBufferMemory");
            Console.WriteLine($"Alloc buffer: {size} bytes");

            return new VulkanBuffer(_ctx, buffer, memory, size, hostVisible);
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
        {
            _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var props);

            // Проход 1: точное совпадение
            for (uint i = 0; i < props.MemoryTypeCount; i++)
            {
                if ((typeBits & (1u << (int)i)) == 0) continue;
                var flags = props.MemoryTypes[(int)i].PropertyFlags;
                if ((flags & required) == required)
                    return i;
            }

            // Проход 2: fallback — убираем HostCoherent (не всегда нужен отдельно)
            var relaxed = required & ~MemoryPropertyFlags.HostCoherentBit;
            if (relaxed != required)
            {
                for (uint i = 0; i < props.MemoryTypeCount; i++)
                {
                    if ((typeBits & (1u << (int)i)) == 0) continue;
                    var flags = props.MemoryTypes[(int)i].PropertyFlags;
                    if ((flags & relaxed) == relaxed)
                    {
                        Console.WriteLine($"[VMA] FindMemoryType: используем relaxed тип памяти (без HostCoherent) для {required}");
                        return i;
                    }
                }
            }

            // Проход 3: последний шанс — только DeviceLocal если запрашивали его
            if (required.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                for (uint i = 0; i < props.MemoryTypeCount; i++)
                {
                    if ((typeBits & (1u << (int)i)) == 0) continue;
                    var flags = props.MemoryTypes[(int)i].PropertyFlags;
                    if (flags.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
                    {
                        Console.WriteLine($"[VMA] FindMemoryType: используем DeviceLocal-only тип для {required}");
                        return i;
                    }
                }
            }

            throw new InvalidOperationException(
                $"Не найден подходящий тип памяти. Required: {required}. " +
                $"Доступных типов: {props.MemoryTypeCount}");
        }

        private void CopyBuffer(VulkanBuffer src, VulkanBuffer dst, ulong size)
        {
            var cmd = _ctx.BeginSingleTimeCommands();

            var region = new BufferCopy { Size = size };
            _ctx.Vk.CmdCopyBuffer(cmd, src.Handle, dst.Handle, 1, &region);

            _ctx.EndSingleTimeCommands(cmd);
        }

        private bool DetectUmaMemory()
        {
            // Определяем UMA по типу устройства, а НЕ по флагам памяти.
            //
            // Почему не через флаги: на NVIDIA с Resizable BAR (SAM) существует
            // маленький heap с флагами HostVisible|DeviceLocal — это не UMA,
            // это просто BAR-окно в VRAM (~256MB). Если принять его за UMA
            // и запросить из него большой буфер — получим ErrorOutOfDeviceMemory.
            //
            // Настоящая UMA — только IntegratedGpu (iGPU, мобильники, APU).
            _ctx.Vk.GetPhysicalDeviceProperties(_ctx.PhysicalDevice, out var props);
            bool isIntegrated = props.DeviceType == PhysicalDeviceType.IntegratedGpu;

            if (isIntegrated)
                Console.WriteLine("[VMA] Интегрированный GPU — UMA режим активен.");
            else
                Console.WriteLine($"[VMA] Дискретный GPU ({props.DeviceType}) — используем staging буферы.");

            return isIntegrated;
        }
        // VulkanMemoryAllocator не владеет никакими Vulkan-объектами напрямую
        // (все буферы — VulkanBuffer, они сами IDisposable).
        // IDisposable нужен только чтобы можно было вызвать _vma?.Dispose() из SceneGpu.
        public void Dispose() { }

    }

    /// <summary>
    /// Глобальные push constants — маленький блок данных который передаётся
    /// в шейдер БЕЗ буфера, прямо в command buffer.
    /// Аналог GL.Uniform1f(uAspectRatio) и GL.Uniform1f(uTime).
    ///
    /// Максимум 128 байт гарантировано спецификацией Vulkan.
    /// Используй для: aspectRatio, time, primIndex — всё что меняется каждый кадр.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FramePushConstants
    {
        public float AspectRatio;   // u_aspectRatio
        public float Time;          // u_time
        public int   PrimIndex;     // u_primIndex (для render loop)
        public float Reserved;
    }
}