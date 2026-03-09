// ============================================================
//  VulkanContext.cs
//  EurekaSharp — Vulkan Backend
//
//  Отвечает за:
//    • VkInstance + debug messenger
//    • VkPhysicalDevice выбор (с учётом compute queue)
//    • VkDevice + очереди (graphics + compute, могут совпадать)
//    • VkSurfaceKHR через Silk.NET.Windowing
//    • VkSwapchainKHR + ImageViews
//    • VkRenderPass (single-pass, color attachment)
//    • VkFramebuffers
//    • VkCommandPool + CommandBuffers (один на swapchain image)
//    • VkSemaphores / VkFences для синхронизации кадров
//
//  Намеренно НЕ содержит pipeline, шейдеров и буферов —
//  это зона VulkanAnimationEngine.
//
//  Зависимости NuGet:
//    Silk.NET.Vulkan
//    Silk.NET.Vulkan.Extensions.KHR
//    Silk.NET.Vulkan.Extensions.EXT
//    Silk.NET.Windowing
//    Silk.NET.GLFW          ← для поверхности на Desktop
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace PhysicsSimulation.Rendering.Vulkan
{
    /// <summary>
    /// Хранит все «сырые» Vulkan-объекты и управляет их жизненным циклом.
    /// Один экземпляр на приложение.
    /// </summary>
    public sealed unsafe class VulkanContext : IDisposable
    {
        // ── Public handles (читают VulkanAnimationEngine и др.) ──────────────
        public Vk                  Vk              { get; }
        public Instance            Instance        { get; private set; }
        public PhysicalDevice      PhysicalDevice  { get; private set; }
        public Device              Device          { get; private set; }
        public Queue               GraphicsQueue   { get; private set; }
        public Queue               ComputeQueue    { get; private set; }
        public Queue               PresentQueue    { get; private set; }
        public uint                GraphicsFamily  { get; private set; }
        public uint                ComputeFamily   { get; private set; }
        public uint                PresentFamily   { get; private set; }
        public SurfaceKHR          Surface         { get; private set; }
        public SwapchainKHR        Swapchain       { get; private set; }
        public Format              SwapchainFormat { get; private set; }
        public Extent2D            SwapchainExtent { get; private set; }
        public Image[]             SwapchainImages { get; private set; } = [];
        public ImageView[]         SwapchainViews  { get; private set; } = [];
        public Framebuffer[]       Framebuffers    { get; private set; } = [];
        public RenderPass          RenderPass      { get; private set; }
        public CommandPool         CommandPool     { get; private set; }
        public CommandBuffer[]     CommandBuffers  { get; private set; } = [];

        // Per-frame sync
        // ┌─────────────────────────────────────────────────────────────────┐
        // │ Схема синхронизации (MaxFramesInFlight=2, SwapchainImages=3):   │
        // │                                                                  │
        // │  ImageAvailable[CurrentFrame]  — сигналится AcquireNextImage    │
        // │  RenderFinished[imageIndex]    — сигналится Submit,             │
        // │                                  ждётся Present                 │
        // │  InFlightFences[CurrentFrame]  — ждётся перед новым кадром      │
        // │  _imagesInFlight[imageIndex]   — какой fence охраняет image     │
        // │                                                                  │
        // │ RenderFinished индексируется по imageIndex, а не по CurrentFrame │
        // │ потому что Present асинхронный: fence не гарантирует что        │
        // │ presentation уже потребил RenderFinished[CurrentFrame].          │
        // │ Image не может быть в presentation дважды одновременно —        │
        // │ поэтому RenderFinished[imageIndex] всегда безопасен.            │
        // └─────────────────────────────────────────────────────────────────┘
        public const int           MaxFramesInFlight = 2;
        public Silk.NET.Vulkan.Semaphore[] ImageAvailable  { get; private set; } = [];
        public Silk.NET.Vulkan.Semaphore[] RenderFinished  { get; private set; } = [];
        public Fence[]                     InFlightFences  { get; private set; } = [];
        public int                         CurrentFrame    { get; private set; }

        // _imagesInFlight[imageIndex] = fence который в данный момент рендерит этот image.
        // Нужен чтобы не начинать рендеринг image пока предыдущий Submit для него не завершён.
        private Fence[] _imagesInFlight = [];

        // Extensions
        private KhrSurface?        _khrSurface;
        private KhrSwapchain?      _khrSwapchain;
        private ExtDebugUtils?     _extDebug;
        private DebugUtilsMessengerEXT _debugMessenger;

        // Window (Silk.NET)
        private readonly IView     _view;
        private readonly bool      _enableValidation;

        private bool _disposed;

        // ── Validation layers ─────────────────────────────────────────────────
        private static readonly string[] ValidationLayers =
        [
            "VK_LAYER_KHRONOS_validation"
        ];

        private static readonly string[] DeviceExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        // ─────────────────────────────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────────────────────────────

        public VulkanContext(IView view, bool enableValidation = true)
        {
            _view             = view ?? throw new ArgumentNullException(nameof(view));
            _enableValidation = enableValidation;
            Vk                = Vk.GetApi();

            CreateInstance();
            SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateCommandPool();
            CreateSwapchain();
            CreateRenderPass();
            CreateFramebuffers();
            AllocateCommandBuffers();
            CreateSyncObjects();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  1. Instance
        // ─────────────────────────────────────────────────────────────────────

        private void CreateInstance()
        {
            if (_enableValidation && !CheckValidationLayerSupport())
                throw new InvalidOperationException(
                    "Vulkan validation layers запрошены, но недоступны. " +
                    "Установи Vulkan SDK или передай enableValidation: false.");

            var appInfo = new ApplicationInfo
            {
                SType              = StructureType.ApplicationInfo,
                PApplicationName   = (byte*)Marshal.StringToHGlobalAnsi("EurekaSharp"),
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName        = (byte*)Marshal.StringToHGlobalAnsi("EurekaEngine"),
                EngineVersion      = new Version32(1, 0, 0),
                ApiVersion         = Vk.Version12   // Vulkan 1.2 — доступен на Android 10+
            };

            var requiredExts = GetRequiredInstanceExtensions();

            using var extPtrs  = new NativeStringArray(requiredExts);
            using var layerPtrs = _enableValidation
                ? new NativeStringArray(ValidationLayers)
                : new NativeStringArray([]);

            var createInfo = new InstanceCreateInfo
            {
                SType                   = StructureType.InstanceCreateInfo,
                PApplicationInfo        = &appInfo,
                EnabledExtensionCount   = (uint)requiredExts.Length,
                PpEnabledExtensionNames = extPtrs,
                EnabledLayerCount       = _enableValidation ? (uint)ValidationLayers.Length : 0,
                PpEnabledLayerNames     = _enableValidation ? (byte**)layerPtrs : null
            };

            Check(Vk.CreateInstance(&createInfo, null, out var inst), "CreateInstance");
            Instance = inst;

            Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
            Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        }

        private string[] GetRequiredInstanceExtensions()
        {
            // Silk.NET даёт нам расширения нужные для поверхности окна
            var exts = _view.VkSurface!.GetRequiredExtensions(out uint count);
            var result = new List<string>();
            for (uint i = 0; i < count; i++)
                result.Add(Marshal.PtrToStringAnsi((IntPtr)exts[i])!);

            if (_enableValidation)
                result.Add(ExtDebugUtils.ExtensionName);

            return result.ToArray();
        }

        private bool CheckValidationLayerSupport()
        {
            uint count = 0;
            Vk.EnumerateInstanceLayerProperties(&count, null);
            var layers = new LayerProperties[count];
            fixed (LayerProperties* p = layers)
                Vk.EnumerateInstanceLayerProperties(&count, p);

            return ValidationLayers.All(vl =>
                layers.Any(l => Marshal.PtrToStringAnsi((IntPtr)l.LayerName) == vl));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  2. Debug messenger
        // ─────────────────────────────────────────────────────────────────────

        private void SetupDebugMessenger()
        {
            if (!_enableValidation) return;
            if (!Vk.TryGetInstanceExtension(Instance, out _extDebug)) return;

            var createInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType           = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                  DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType     = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt     |
                                  DebugUtilsMessageTypeFlagsEXT.ValidationBitExt  |
                                  DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new DebugUtilsMessengerCallbackFunctionEXT(VulkanDebugCallback)
            };

            Check(_extDebug!.CreateDebugUtilsMessenger(Instance, &createInfo, null, out _debugMessenger),
                  "CreateDebugUtilsMessenger");
        }

        private static uint VulkanDebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT     type,
            DebugUtilsMessengerCallbackDataEXT* data,
            void* userData)
        {
            string msg = Marshal.PtrToStringAnsi((IntPtr)data->PMessage) ?? "(null)";
            string prefix = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt)
                ? "[VK ERROR]"
                : "[VK WARN ]";
            Console.Error.WriteLine($"{prefix} {msg}");
            return Vk.False;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  3. Surface (платформонезависимый через Silk.NET.Windowing)
        // ─────────────────────────────────────────────────────────────────────

        private void CreateSurface()
        {
            Surface = _view.VkSurface!.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
            if (!Vk.TryGetInstanceExtension(Instance, out _khrSurface))
                throw new InvalidOperationException("KHR_surface расширение недоступно.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  4. Physical device
        // ─────────────────────────────────────────────────────────────────────

        private void PickPhysicalDevice()
        {
            uint count = 0;
            Vk.EnumeratePhysicalDevices(Instance, &count, null);
            if (count == 0) throw new InvalidOperationException("Vulkan-совместимые GPU не найдены.");

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = devices)
                Vk.EnumeratePhysicalDevices(Instance, &count, p);

            // Выбираем лучший: prefer discrete GPU с compute queue
            PhysicalDevice = devices
                .OrderByDescending(ScoreDevice)
                .First(d => IsDeviceSuitable(d));

            Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var props);
            string name = Marshal.PtrToStringAnsi((IntPtr)props.DeviceName) ?? "Unknown";
            Console.WriteLine($"[Vulkan] Выбран GPU: {name}");
        }

        private int ScoreDevice(PhysicalDevice device)
        {
            Vk.GetPhysicalDeviceProperties(device, out var props);
            int score = props.DeviceType == PhysicalDeviceType.DiscreteGpu ? 1000 : 0;
            score += (int)(props.Limits.MaxImageDimension2D / 1000);
            return score;
        }

        private bool IsDeviceSuitable(PhysicalDevice device)
        {
            var families = FindQueueFamilies(device);
            if (!families.IsComplete) return false;

            bool extsOk = CheckDeviceExtensionSupport(device);
            if (!extsOk) return false;

            QuerySwapchainSupport(device, out var formats, out var modes);
            return formats.Length > 0 && modes.Length > 0;
        }

        private bool CheckDeviceExtensionSupport(PhysicalDevice device)
        {
            uint count = 0;
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
            var exts = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = exts)
                Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p);

            var available = exts
                .Select(e => Marshal.PtrToStringAnsi((IntPtr)e.ExtensionName))
                .ToHashSet();

            return DeviceExtensions.All(e => available.Contains(e));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  5. Queue families
        // ─────────────────────────────────────────────────────────────────────

        private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
        {
            uint count = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
            var families = new QueueFamilyProperties[count];
            fixed (QueueFamilyProperties* p = families)
                Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);

            var result = new QueueFamilyIndices();

            for (uint i = 0; i < families.Length; i++)
            {
                var f = families[i];

                // Graphics queue
                if (f.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                    result.Graphics = i;

                // Compute queue — предпочитаем выделенную (без graphics)
                if (f.QueueFlags.HasFlag(QueueFlags.ComputeBit))
                {
                    if (!f.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                        result.Compute = i;         // выделенный compute
                    else if (!result.Compute.HasValue)
                        result.Compute = i;         // shared, но лучше чем ничего
                }

                // Present queue
                _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var presentSupport);
                if (presentSupport) result.Present = i;

                if (result.IsComplete) break;
            }

            return result;
        }

        private struct QueueFamilyIndices
        {
            public uint? Graphics;
            public uint? Compute;
            public uint? Present;
            public readonly bool IsComplete =>
                Graphics.HasValue && Compute.HasValue && Present.HasValue;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  6. Logical device + queues
        // ─────────────────────────────────────────────────────────────────────

        private void CreateLogicalDevice()
        {
            var families = FindQueueFamilies(PhysicalDevice);
            GraphicsFamily = families.Graphics!.Value;
            ComputeFamily  = families.Compute!.Value;
            PresentFamily  = families.Present!.Value;

            // Уникальные семейства — создаём только один DeviceQueue на каждое
            var uniqueFamilies = new HashSet<uint> { GraphicsFamily, ComputeFamily, PresentFamily };
            float priority = 1.0f;

            var uniqueFamiliesArr = uniqueFamilies.ToArray();
            var queueInfos = new DeviceQueueCreateInfo[uniqueFamiliesArr.Length];

            for (int qi = 0; qi < uniqueFamiliesArr.Length; qi++)
            {
                queueInfos[qi] = new DeviceQueueCreateInfo
                {
                    SType            = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = uniqueFamiliesArr[qi],
                    QueueCount       = 1,
                    PQueuePriorities = &priority
                };
            }

            // Включаем нужные фичи
            var features = new PhysicalDeviceFeatures
            {
                // Для работы с геометрией как на desktop, так и на мобилках
                // FillModeNonSolid нужен если хотим wireframe — опционально
            };

            // Vulkan12Features убраны — DescriptorIndexing/TimelineSemaphore не нужны
            // и могут не поддерживаться на части GPU, ломая device creation.

            using var extPtrs = new NativeStringArray(DeviceExtensions);
            using var layerPtrs = _enableValidation
                ? new NativeStringArray(ValidationLayers)
                : new NativeStringArray([]);

            fixed (DeviceQueueCreateInfo* pQueues = queueInfos)
            {
                var createInfo = new DeviceCreateInfo
                {
                    SType                   = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount    = (uint)queueInfos.Length,
                    PQueueCreateInfos       = pQueues,
                    PEnabledFeatures        = &features,
                    EnabledExtensionCount   = (uint)DeviceExtensions.Length,
                    PpEnabledExtensionNames = extPtrs,
                    EnabledLayerCount       = _enableValidation ? (uint)ValidationLayers.Length : 0,
                    PpEnabledLayerNames     = _enableValidation ? (byte**)layerPtrs : null
                };

                Check(Vk.CreateDevice(PhysicalDevice, &createInfo, null, out var device), "CreateDevice");
                Device = device;
            }

            Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out var gq);
            Vk.GetDeviceQueue(Device, ComputeFamily,  0, out var cq);
            Vk.GetDeviceQueue(Device, PresentFamily,  0, out var pq);
            GraphicsQueue = gq;
            ComputeQueue  = cq;
            PresentQueue  = pq;

            if (!Vk.TryGetDeviceExtension(Instance, Device, out _khrSwapchain))
                throw new InvalidOperationException("KHR_swapchain расширение недоступно.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  7. Command pool
        // ─────────────────────────────────────────────────────────────────────

        private void CreateCommandPool()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType            = StructureType.CommandPoolCreateInfo,
                Flags            = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = GraphicsFamily
            };
            Check(Vk.CreateCommandPool(Device, &poolInfo, null, out var pool), "CreateCommandPool");
            CommandPool = pool;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  8. Swapchain
        // ─────────────────────────────────────────────────────────────────────

        private void CreateSwapchain()
        {
            QuerySwapchainSupport(PhysicalDevice, out var formats, out var modes);

            var surfaceCaps = new SurfaceCapabilitiesKHR();
            _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(PhysicalDevice, Surface, &surfaceCaps);

            var format  = ChooseSurfaceFormat(formats);
            var mode    = ChoosePresentMode(modes);
            var extent  = ChooseSwapExtent(surfaceCaps);

            uint imageCount = surfaceCaps.MinImageCount + 1;
            if (surfaceCaps.MaxImageCount > 0 && imageCount > surfaceCaps.MaxImageCount)
                imageCount = surfaceCaps.MaxImageCount;

            var indices = stackalloc uint[] { GraphicsFamily, PresentFamily };
            bool sameFamily = GraphicsFamily == PresentFamily;

            var createInfo = new SwapchainCreateInfoKHR
            {
                SType            = StructureType.SwapchainCreateInfoKhr,
                Surface          = Surface,
                MinImageCount    = imageCount,
                ImageFormat      = format.Format,
                ImageColorSpace  = format.ColorSpace,
                ImageExtent      = extent,
                ImageArrayLayers = 1,
                ImageUsage       = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode    = sameFamily ? SharingMode.Exclusive : SharingMode.Concurrent,
                QueueFamilyIndexCount = sameFamily ? 0u : 2u,
                PQueueFamilyIndices   = sameFamily ? null : indices,
                PreTransform     = surfaceCaps.CurrentTransform,
                CompositeAlpha   = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode      = mode,
                Clipped          = true,
                OldSwapchain     = default
            };

            Check(_khrSwapchain!.CreateSwapchain(Device, &createInfo, null, out var swapchain), "CreateSwapchain");
            Swapchain       = swapchain;
            SwapchainFormat = format.Format;
            SwapchainExtent = extent;

            // Получаем images
            uint imgCount = 0;
            _khrSwapchain.GetSwapchainImages(Device, Swapchain, &imgCount, null);
            SwapchainImages = new Image[imgCount];
            fixed (Image* p = SwapchainImages)
                _khrSwapchain.GetSwapchainImages(Device, Swapchain, &imgCount, p);

            // Создаём image views
            SwapchainViews = new ImageView[imgCount];
            for (int i = 0; i < imgCount; i++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType    = StructureType.ImageViewCreateInfo,
                    Image    = SwapchainImages[i],
                    ViewType = ImageViewType.Type2D,
                    Format   = SwapchainFormat,
                    Components = new ComponentMapping
                    {
                        R = ComponentSwizzle.Identity,
                        G = ComponentSwizzle.Identity,
                        B = ComponentSwizzle.Identity,
                        A = ComponentSwizzle.Identity
                    },
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask     = ImageAspectFlags.ColorBit,
                        BaseMipLevel   = 0,
                        LevelCount     = 1,
                        BaseArrayLayer = 0,
                        LayerCount     = 1
                    }
                };
                Check(Vk.CreateImageView(Device, &viewInfo, null, out SwapchainViews[i]), "CreateImageView");
            }
        }

        private void QuerySwapchainSupport(
            PhysicalDevice device,
            out SurfaceFormatKHR[] formats,
            out PresentModeKHR[] modes)
        {
            uint count = 0;
            _khrSurface!.GetPhysicalDeviceSurfaceFormats(device, Surface, &count, null);
            formats = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* p = formats)
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, Surface, &count, p);

            _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, Surface, &count, null);
            modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* p = modes)
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, Surface, &count, p);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
        {
            // Вариант 1 — самый простой: всегда берём первый UNORM (часто это B8G8R8A8_UNORM)
            // return formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, formats[0]);

            // Вариант 2 — более надёжный: ищем BGRA8_UNORM, если нет — любой UNORM, если нет — первый
            foreach (var f in formats)
            {
                if (f.Format == Format.B8G8R8A8Unorm &&
                    f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)   // цветовое пространство оставляем sRGB
                    return f;
            }

            foreach (var f in formats)
            {
                if (f.Format == Format.B8G8R8A8Unorm ||
                    f.Format == Format.R8G8B8A8Unorm)
                    return f;
            }

            // fallback — что первое придёт
            return formats[0];
        }

        private static PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes)
        {
            // Mailbox = triple buffering без tearing (лучший для desktop)
            // FIFO = VSync (гарантированно есть везде, включая Android)
            return modes.Contains(PresentModeKHR.MailboxKhr)
                ? PresentModeKHR.MailboxKhr
                : PresentModeKHR.FifoKhr;
        }

        private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR caps)
        {
            if (caps.CurrentExtent.Width != uint.MaxValue)
                return caps.CurrentExtent;

            return new Extent2D
            {
                Width  = Math.Clamp((uint)_view.FramebufferSize.X,
                             caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp((uint)_view.FramebufferSize.Y,
                             caps.MinImageExtent.Height, caps.MaxImageExtent.Height)
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  9. RenderPass
        //     Single subpass: color attachment → PRESENT_SRC
        // ─────────────────────────────────────────────────────────────────────

        private void CreateRenderPass()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format         = SwapchainFormat,
                Samples        = SampleCountFlags.Count1Bit,
                LoadOp         = AttachmentLoadOp.Clear,         // аналог GL.Clear
                StoreOp        = AttachmentStoreOp.Store,
                StencilLoadOp  = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout  = ImageLayout.Undefined,
                FinalLayout    = ImageLayout.PresentSrcKhr
            };

            var colorRef = new AttachmentReference
            {
                Attachment = 0,
                Layout     = ImageLayout.ColorAttachmentOptimal
            };

            var subpass = new SubpassDescription
            {
                PipelineBindPoint    = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments    = &colorRef
            };

            // Subpass dependency: ждём пока swapchain image станет доступен для записи
            var dependency = new SubpassDependency
            {
                SrcSubpass    = Vk.SubpassExternal,
                DstSubpass    = 0,
                SrcStageMask  = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = 0,
                DstStageMask  = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit
            };

            var renderPassInfo = new RenderPassCreateInfo
            {
                SType           = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments    = &colorAttachment,
                SubpassCount    = 1,
                PSubpasses      = &subpass,
                DependencyCount = 1,
                PDependencies   = &dependency
            };

            Check(Vk.CreateRenderPass(Device, &renderPassInfo, null, out var rp), "CreateRenderPass");
            RenderPass = rp;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  10. Framebuffers (по одному на каждый swapchain image)
        // ─────────────────────────────────────────────────────────────────────

        private void CreateFramebuffers()
        {
            Framebuffers = new Framebuffer[SwapchainViews.Length];
            for (int i = 0; i < SwapchainViews.Length; i++)
            {
                var attachment = SwapchainViews[i];
                var fbInfo = new FramebufferCreateInfo
                {
                    SType           = StructureType.FramebufferCreateInfo,
                    RenderPass      = RenderPass,
                    AttachmentCount = 1,
                    PAttachments    = &attachment,
                    Width           = SwapchainExtent.Width,
                    Height          = SwapchainExtent.Height,
                    Layers          = 1
                };
                Check(Vk.CreateFramebuffer(Device, &fbInfo, null, out Framebuffers[i]), "CreateFramebuffer");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  11. Command buffers
        // ─────────────────────────────────────────────────────────────────────

        private void AllocateCommandBuffers()
        {
            CommandBuffers = new CommandBuffer[SwapchainImages.Length];
            fixed (CommandBuffer* p = CommandBuffers)
            {
                var allocInfo = new CommandBufferAllocateInfo
                {
                    SType              = StructureType.CommandBufferAllocateInfo,
                    CommandPool        = CommandPool,
                    Level              = CommandBufferLevel.Primary,
                    CommandBufferCount = (uint)CommandBuffers.Length
                };
                Check(Vk.AllocateCommandBuffers(Device, &allocInfo, p), "AllocateCommandBuffers");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  12. Sync objects
        // ─────────────────────────────────────────────────────────────────────

        private void CreateSyncObjects()
        {
            int imgCount = SwapchainImages.Length;

            // ImageAvailable: по CurrentFrame (MaxFramesInFlight штук)
            // RenderFinished: по imageIndex (SwapchainImages.Length штук) — см. комментарий к полям
            ImageAvailable  = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
            RenderFinished  = new Silk.NET.Vulkan.Semaphore[imgCount];
            InFlightFences  = new Fence[MaxFramesInFlight];
            _imagesInFlight = new Fence[imgCount]; // инициализируются как null (default(Fence))

            var semInfo   = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit  // стартуем сигналом — иначе первый кадр зависнет
            };

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                Check(Vk.CreateSemaphore(Device, &semInfo,   null, out ImageAvailable[i]),   "CreateSemaphore (imageAvailable)");
                Check(Vk.CreateFence    (Device, &fenceInfo, null, out InFlightFences[i]),   "CreateFence");
            }
            for (int i = 0; i < imgCount; i++)
                Check(Vk.CreateSemaphore(Device, &semInfo, null, out RenderFinished[i]), "CreateSemaphore (renderFinished)");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Frame helpers (используются VulkanSceneGpu)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ждёт fence текущего кадра, получает следующий swapchain image.
        /// Возвращает индекс image или -1 если swapchain устарел (resize).
        /// </summary>
        public int BeginFrame()
        {
            // Шаг 1: ждём fence текущего frame-slot'а
            fixed (Fence* pFence = &InFlightFences[CurrentFrame])
                Vk.WaitForFences(Device, 1, pFence, true, ulong.MaxValue);

            // Шаг 2: получаем следующий swapchain image
            uint imageIndex = 0;
            var result = _khrSwapchain!.AcquireNextImage(
                Device, Swapchain, ulong.MaxValue,
                ImageAvailable[CurrentFrame], default, &imageIndex);

            if (result == Result.ErrorOutOfDateKhr)
                return -1;  // swapchain устарел — нужен RecreateSwapchain

            if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new InvalidOperationException($"AcquireNextImage: {result}");

            // Шаг 3: если этот image ещё рендерится предыдущим кадром — ждём его fence.
            // Возможно если swapchain отдаёт images быстрее чем MaxFramesInFlight.
            var imgFence = _imagesInFlight[(int)imageIndex];
            if (imgFence.Handle != 0)
            {
                // imgFence — локальная value-type, адрес можно брать напрямую в unsafe
                Vk.WaitForFences(Device, 1, &imgFence, true, ulong.MaxValue);
            }
            _imagesInFlight[(int)imageIndex] = InFlightFences[CurrentFrame];

            // Шаг 4: сбрасываем fence — только после всех ожиданий
            fixed (Fence* pFence = &InFlightFences[CurrentFrame])
                Vk.ResetFences(Device, 1, pFence);

            return (int)imageIndex;
        }

        /// <summary>
        /// Отправляет заполненный command buffer на графическую очередь и вызывает Present.
        /// </summary>
        public void EndFrame(int imageIndex)
        {
            var waitSem   = ImageAvailable[CurrentFrame];  // сигналится AcquireNextImage (per CurrentFrame)
            var signalSem = RenderFinished[imageIndex];    // per imageIndex — безопасно т.к. image не в двух Present сразу
            var cmd       = CommandBuffers[imageIndex];
            var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

            var submitInfo = new SubmitInfo
            {
                SType                = StructureType.SubmitInfo,
                WaitSemaphoreCount   = 1,
                PWaitSemaphores      = &waitSem,
                PWaitDstStageMask    = &waitStage,
                CommandBufferCount   = 1,
                PCommandBuffers      = &cmd,
                SignalSemaphoreCount = 1,
                PSignalSemaphores    = &signalSem
            };

            Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, InFlightFences[CurrentFrame]),
                  "QueueSubmit");

            var swapchain = Swapchain;
            var idx       = (uint)imageIndex;
            var presentInfo = new PresentInfoKHR
            {
                SType              = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores    = &signalSem,
                SwapchainCount     = 1,
                PSwapchains        = &swapchain,
                PImageIndices      = &idx
            };

            var result = _khrSwapchain!.QueuePresent(PresentQueue, &presentInfo);
            if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
                RecreateSwapchain();
            else if (result != Result.Success)
                throw new InvalidOperationException($"QueuePresent: {result}");

            CurrentFrame = (CurrentFrame + 1) % MaxFramesInFlight;
        }

        /// <summary>
        /// Пересоздаёт swapchain после resize / потери поверхности.
        /// Вызывай из SceneGpu.SetViewportSize или из EndFrame при OutOfDate.
        /// </summary>
        public void RecreateSwapchain()
        {
            Vk.DeviceWaitIdle(Device);

            CleanupSwapchain();
            CreateSwapchain();
            CreateRenderPass();
            CreateFramebuffers();
            AllocateCommandBuffers();

            // RenderFinished и _imagesInFlight зависят от числа swapchain images — пересоздаём
            foreach (var s in RenderFinished) Vk.DestroySemaphore(Device, s, null);
            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            int imgCount = SwapchainImages.Length;
            RenderFinished  = new Silk.NET.Vulkan.Semaphore[imgCount];
            _imagesInFlight = new Fence[imgCount];
            for (int i = 0; i < imgCount; i++)
                Check(Vk.CreateSemaphore(Device, &semInfo, null, out RenderFinished[i]), "CreateSemaphore (renderFinished, recreate)");
        }

        private void CleanupSwapchain()
        {
            foreach (var fb in Framebuffers) Vk.DestroyFramebuffer(Device, fb, null);
            fixed (CommandBuffer* p = CommandBuffers)
                Vk.FreeCommandBuffers(Device, CommandPool, (uint)CommandBuffers.Length, p);
            Vk.DestroyRenderPass(Device, RenderPass, null);
            foreach (var iv in SwapchainViews) Vk.DestroyImageView(Device, iv, null);
            _khrSwapchain!.DestroySwapchain(Device, Swapchain, null);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  One-shot command buffer (для начальной загрузки данных)
        // ─────────────────────────────────────────────────────────────────────

        public CommandBuffer BeginSingleTimeCommands()
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType              = StructureType.CommandBufferAllocateInfo,
                Level              = CommandBufferLevel.Primary,
                CommandPool        = CommandPool,
                CommandBufferCount = 1
            };
            Vk.AllocateCommandBuffers(Device, &allocInfo, out var cmd);
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            Vk.BeginCommandBuffer(cmd, &beginInfo);
            return cmd;
        }

        public void EndSingleTimeCommands(CommandBuffer cmd)
        {
            Vk.EndCommandBuffer(cmd);
            var submitInfo = new SubmitInfo
            {
                SType              = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers    = &cmd
            };
            Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default);
            Vk.QueueWaitIdle(GraphicsQueue);
            Vk.FreeCommandBuffers(Device, CommandPool, 1, &cmd);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Хелпер: проверка Result
        // ─────────────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Check(Result result, string operation)
        {
            if (result != Result.Success)
                throw new InvalidOperationException($"Vulkan {operation} failed: {result}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Vk.DeviceWaitIdle(Device);

            CleanupSwapchain();

            foreach (var s in ImageAvailable) Vk.DestroySemaphore(Device, s, null);
            foreach (var s in RenderFinished) Vk.DestroySemaphore(Device, s, null);
            foreach (var f in InFlightFences) Vk.DestroyFence(Device, f, null);
            // _imagesInFlight содержит только ссылки на InFlightFences — не уничтожать отдельно

            Vk.DestroyCommandPool(Device, CommandPool, null);
            Vk.DestroyDevice(Device, null);

            if (_enableValidation && _extDebug != null)
                _extDebug.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);

            _khrSurface?.DestroySurface(Instance, Surface, null);
            Vk.DestroyInstance(Instance, null);
            Vk.Dispose();
        }
    }

    // ── Вспомогательный: обёртка над массивом нативных строк ─────────────────
    // (чтобы не держать в памяти HGlobal вручную)
    internal sealed unsafe class NativeStringArray : IDisposable
    {
        private readonly IntPtr[] _ptrs;
        private readonly GCHandle _handle;
        private readonly byte**   _raw;

        public NativeStringArray(string[] strings)
        {
            _ptrs = strings.Select(s => Marshal.StringToHGlobalAnsi(s)).ToArray();
            _handle = GCHandle.Alloc(_ptrs, GCHandleType.Pinned);
            _raw    = (byte**)_handle.AddrOfPinnedObject();
        }

        public static implicit operator byte**(NativeStringArray a) => a._raw;

        public void Dispose()
        {
            _handle.Free();
            foreach (var p in _ptrs) Marshal.FreeHGlobal(p);
        }
    }
}