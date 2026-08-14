using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class Window : WindowBase, IDisposable
    {
        private const int SurfaceWidth = 1280;
        private const int SurfaceHeight = 720;

        private readonly VulkanRenderer _gd;
        private readonly SurfaceKHR _surface;
        private readonly PhysicalDevice _physicalDevice;
        private readonly Device _device;
        private SwapchainKHR _swapchain;

        private Image[] _swapchainImages;
        private TextureView[] _swapchainImageViews;

        private Semaphore[] _imageAvailableSemaphores;
        private Semaphore[] _renderFinishedSemaphores;
        private int _disposeState;

        private int _frameIndex;

        private int _width;
        private int _height;
        private bool _vsyncEnabled;
        private bool _swapchainIsDirty;
        private VkFormat _format;
        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private IPostProcessingEffect _effect;
        private IScalingFilter _scalingFilter;
        private bool _isLinear;
        private float _scalingFilterLevel;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _colorSpacePassthroughEnabled;

        public Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;
        }

        public void Initialize()
        {
            CreateSwapchain();
        }

        private void RecreateSwapchain()
        {
            _gd.Api.DeviceWaitIdle(_device);

            TextureView[] oldSwapchainImageViews = _swapchainImageViews;
            Semaphore[] oldImageAvailableSemaphores = _imageAvailableSemaphores;
            Semaphore[] oldRenderFinishedSemaphores = _renderFinishedSemaphores;
            SwapchainKHR oldSwapchain = _swapchain;

            // Detach the old generation before destroying it. If creating the new
            // generation fails, Dispose will only see the newly-created partial
            // handles and cannot destroy the old generation a second time.
            _swapchainImages = null;
            _swapchainImageViews = null;
            _imageAvailableSemaphores = null;
            _renderFinishedSemaphores = null;
            _swapchain = default;

            unsafe
            {
                if (oldRenderFinishedSemaphores != null)
                {
                    for (int i = oldRenderFinishedSemaphores.Length - 1; i >= 0; i--)
                    {
                        if (oldRenderFinishedSemaphores[i].Handle != 0)
                        {
                            _gd.Api.DestroySemaphore(_device, oldRenderFinishedSemaphores[i], null);
                        }
                    }
                }

                if (oldImageAvailableSemaphores != null)
                {
                    for (int i = oldImageAvailableSemaphores.Length - 1; i >= 0; i--)
                    {
                        if (oldImageAvailableSemaphores[i].Handle != 0)
                        {
                            _gd.Api.DestroySemaphore(_device, oldImageAvailableSemaphores[i], null);
                        }
                    }
                }
            }

            if (oldSwapchainImageViews != null)
            {
                for (int i = oldSwapchainImageViews.Length - 1; i >= 0; i--)
                {
                    oldSwapchainImageViews[i]?.Dispose();
                }
            }

            if (oldSwapchain.Handle != 0)
            {
                _gd.SwapchainApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);
            }

            CreateSwapchain();
            _swapchainIsDirty = false;
        }

        private unsafe void CreateSwapchain()
        {
            _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities).ThrowOnError();

            uint surfaceFormatsCount;

            _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null).ThrowOnError();

            var surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];

            fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats).ThrowOnError();
            }

            uint presentModesCount;

            _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, null).ThrowOnError();

            var presentModes = new PresentModeKHR[presentModesCount];

            fixed (PresentModeKHR* pPresentModes = presentModes)
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, pPresentModes).ThrowOnError();
            }

            uint imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            {
                imageCount = capabilities.MaxImageCount;
            }

            var surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled);

            var extent = ChooseSwapExtent(capabilities);

            _width = (int)extent.Width;
            _height = (int)extent.Height;
            _format = surfaceFormat.Format;

            var swapchainCreateInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.StorageBit,
                ImageSharingMode = SharingMode.Exclusive,
                ImageArrayLayers = 1,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                PresentMode = ChooseSwapPresentMode(presentModes, _vsyncEnabled),
                Clipped = true,
            };

            var textureCreateInfo = new TextureCreateInfo(
                _width,
                _height,
                1,
                1,
                1,
                1,
                1,
                1,
                FormatTable.GetFormat(surfaceFormat.Format),
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            _gd.SwapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out SwapchainKHR swapchain).ThrowOnError();
            _swapchain = swapchain;

            _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, null).ThrowOnError();

            _swapchainImages = new Image[imageCount];

            fixed (Image* pSwapchainImages = _swapchainImages)
            {
                _gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, pSwapchainImages).ThrowOnError();
            }

            _swapchainImageViews = new TextureView[imageCount];

            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                _swapchainImageViews[i] = CreateSwapchainImageView(_swapchainImages[i], surfaceFormat.Format, textureCreateInfo);
            }

            var semaphoreCreateInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };

            _imageAvailableSemaphores = new Semaphore[imageCount];

            for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
            {
                _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out Semaphore semaphore).ThrowOnError();
                _imageAvailableSemaphores[i] = semaphore;
            }

            _renderFinishedSemaphores = new Semaphore[imageCount];

            for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            {
                _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out Semaphore semaphore).ThrowOnError();
                _renderFinishedSemaphores[i] = semaphore;
            }
        }

        private unsafe TextureView CreateSwapchainImageView(Image swapchainImage, VkFormat format, TextureCreateInfo info)
        {
            var componentMapping = new ComponentMapping(
                ComponentSwizzle.R,
                ComponentSwizzle.G,
                ComponentSwizzle.B,
                ComponentSwizzle.A);

            var aspectFlags = ImageAspectFlags.ColorBit;

            var subresourceRange = new ImageSubresourceRange(aspectFlags, 0, 1, 0, 1);

            var imageCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapchainImage,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = componentMapping,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CreateImageView(_device, in imageCreateInfo, null, out var imageView).ThrowOnError();

            return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, imageView), info, format);
        }

        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
        {
            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                return new SurfaceFormatKHR(VkFormat.B8G8R8A8Unorm, ColorSpaceKHR.PaceSrgbNonlinearKhr);
            }

            var formatToReturn = availableFormats[0];
            if (colorSpacePassthroughEnabled)
            {
                foreach (var format in availableFormats)
                {
                    if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                    {
                        formatToReturn = format;
                        break;
                    }
                    else if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.PaceSrgbNonlinearKhr)
                    {
                        formatToReturn = format;
                    }
                }
            }
            else
            {
                foreach (var format in availableFormats)
                {
                    if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.PaceSrgbNonlinearKhr)
                    {
                        formatToReturn = format;
                        break;
                    }
                }
            }

            return formatToReturn;
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supportedFlags)
        {
            if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr))
            {
                return CompositeAlphaFlagsKHR.OpaqueBitKhr;
            }
            else if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.PreMultipliedBitKhr))
            {
                return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
            }
            else
            {
                return CompositeAlphaFlagsKHR.InheritBitKhr;
            }
        }

        private static PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes, bool vsyncEnabled)
        {
            if (!vsyncEnabled && availablePresentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                return PresentModeKHR.ImmediateKhr;
            }
            else if (availablePresentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                return PresentModeKHR.MailboxKhr;
            }
            else
            {
                return PresentModeKHR.FifoKhr;
            }
        }

        public static Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }

            uint width = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, SurfaceWidth));
            uint height = Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, SurfaceHeight));

            return new Extent2D(width, height);
        }

        public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            _gd.PipelineInternal.AutoFlush.Present();

            uint nextImage = 0;
            int semaphoreIndex = _frameIndex++ % _imageAvailableSemaphores.Length;

            while (true)
            {
                var acquireResult = _gd.SwapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    ulong.MaxValue,
                    _imageAvailableSemaphores[semaphoreIndex],
                    new Fence(),
                    ref nextImage);

                if (acquireResult == Result.ErrorOutOfDateKhr ||
                    acquireResult == Result.SuboptimalKhr ||
                    _swapchainIsDirty)
                {
                    RecreateSwapchain();
                    semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                }
                else
                {
                    acquireResult.ThrowOnError();
                    break;
                }
            }

            var swapchainImage = _swapchainImages[nextImage];

            _gd.FlushAllCommands();

            var cbs = _gd.CommandBufferPool.Rent();

            Transition(
                cbs.CommandBuffer,
                swapchainImage,
                0,
                AccessFlags.TransferWriteBit,
                ImageLayout.Undefined,
                ImageLayout.General);

            var view = (TextureView)texture;

            UpdateEffect();

            if (_effect != null)
            {
                view = _effect.Run(view, cbs, _width, _height);
            }

            int srcX0, srcX1, srcY0, srcY1;

            if (crop.Left == 0 && crop.Right == 0)
            {
                srcX0 = 0;
                srcX1 = view.Width;
            }
            else
            {
                srcX0 = crop.Left;
                srcX1 = crop.Right;
            }

            if (crop.Top == 0 && crop.Bottom == 0)
            {
                srcY0 = 0;
                srcY1 = view.Height;
            }
            else
            {
                srcY0 = crop.Top;
                srcY1 = crop.Bottom;
            }

            if (ScreenCaptureRequested)
            {
                if (_effect != null)
                {
                    _gd.CommandBufferPool.Return(
                        cbs,
                        null,
                        stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit },
                        null);
                    _gd.FlushAllCommands();
                    cbs.GetFence().Wait();
                    cbs = _gd.CommandBufferPool.Rent();
                }

                CaptureFrame(view, srcX0, srcY0, srcX1 - srcX0, srcY1 - srcY0, view.Info.Format.IsBgr(), crop.FlipX, crop.FlipY);

                ScreenCaptureRequested = false;
            }

            float ratioX = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _height * crop.AspectRatioX / (_width * crop.AspectRatioY));
            float ratioY = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _width * crop.AspectRatioY / (_height * crop.AspectRatioX));

            int dstWidth = (int)(_width * ratioX);
            int dstHeight = (int)(_height * ratioY);

            int dstPaddingX = (_width - dstWidth) / 2;
            int dstPaddingY = (_height - dstHeight) / 2;

            int dstX0 = crop.FlipX ? _width - dstPaddingX : dstPaddingX;
            int dstX1 = crop.FlipX ? dstPaddingX : _width - dstPaddingX;

            int dstY0 = crop.FlipY ? dstPaddingY : _height - dstPaddingY;
            int dstY1 = crop.FlipY ? _height - dstPaddingY : dstPaddingY;

            if (_scalingFilter != null)
            {
                _scalingFilter.Run(
                    view,
                    cbs,
                    _swapchainImageViews[nextImage].GetImageViewForAttachment(),
                    _format,
                    _width,
                    _height,
                    new Extents2D(srcX0, srcY0, srcX1, srcY1),
                    new Extents2D(dstX0, dstY0, dstX1, dstY1)
                    );
            }
            else
            {
                _gd.HelperShader.BlitColor(
                    _gd,
                    cbs,
                    view,
                    _swapchainImageViews[nextImage],
                    new Extents2D(srcX0, srcY0, srcX1, srcY1),
                    new Extents2D(dstX0, dstY1, dstX1, dstY0),
                    _isLinear,
                    true);
            }

            Transition(
                cbs.CommandBuffer,
                swapchainImage,
                0,
                0,
                ImageLayout.General,
                ImageLayout.PresentSrcKhr);

            _gd.CommandBufferPool.Return(
                cbs,
                stackalloc[] { _imageAvailableSemaphores[semaphoreIndex] },
                stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit },
                stackalloc[] { _renderFinishedSemaphores[semaphoreIndex] });

            // TODO: Present queue.
            var semaphore = _renderFinishedSemaphores[semaphoreIndex];
            var swapchain = _swapchain;

            Result result;

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &semaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &nextImage,
                PResults = &result,
            };

            lock (_gd.QueueLock)
            {
                _gd.SwapchainApi.QueuePresent(_gd.Queue, in presentInfo);
            }
        }

        public override void SetAntiAliasing(AntiAliasing effect)
        {
            if (_currentAntiAliasing == effect && _effect != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public override void SetScalingFilter(ScalingFilter type)
        {
            if (_currentScalingFilter == type && _effect != null)
            {
                return;
            }

            _currentScalingFilter = type;

            _updateScalingFilter = true;
        }

        public override void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled)
        {
            _colorSpacePassthroughEnabled = colorSpacePassthroughEnabled;
            _swapchainIsDirty = true;
        }

        private void UpdateEffect()
        {
            if (_updateEffect)
            {
                _updateEffect = false;

                switch (_currentAntiAliasing)
                {
                    case AntiAliasing.Fxaa:
                        _effect?.Dispose();
                        _effect = new FxaaPostProcessingEffect(_gd, _device);
                        break;
                    case AntiAliasing.None:
                        _effect?.Dispose();
                        _effect = null;
                        break;
                    case AntiAliasing.SmaaLow:
                    case AntiAliasing.SmaaMedium:
                    case AntiAliasing.SmaaHigh:
                    case AntiAliasing.SmaaUltra:
                        var quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                        if (_effect is SmaaPostProcessingEffect smaa)
                        {
                            smaa.Quality = quality;
                        }
                        else
                        {
                            _effect?.Dispose();
                            _effect = new SmaaPostProcessingEffect(_gd, _device, quality);
                        }
                        break;
                }
            }

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

                switch (_currentScalingFilter)
                {
                    case ScalingFilter.Bilinear:
                    case ScalingFilter.Nearest:
                        _scalingFilter?.Dispose();
                        _scalingFilter = null;
                        _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                        break;
                    case ScalingFilter.Fsr:
                        if (_scalingFilter is not FsrScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new FsrScalingFilter(_gd, _device);
                        }

                        _scalingFilter.Level = _scalingFilterLevel;
                        break;
                    case ScalingFilter.Area:
                        if (_scalingFilter is not AreaScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new AreaScalingFilter(_gd, _device);
                        }
                        break;
                }
            }
        }

        public override void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
        }

        private unsafe void Transition(
            CommandBuffer commandBuffer,
            Image image,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            ImageLayout srcLayout,
            ImageLayout dstLayout)
        {
            var subresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);
        }

        private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            byte[] bitmap = texture.GetData(x, y, width, height);

            _gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
        }

        public override void SetSize(int width, int height)
        {
            // We don't need to use width and height as we can get the size from the surface.
            _swapchainIsDirty = true;
        }

        public override void ChangeVSyncMode(bool vsyncEnabled)
        {
            _vsyncEnabled = vsyncEnabled;
            _swapchainIsDirty = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            TextureView[] swapchainImageViews = _swapchainImageViews;
            Semaphore[] imageAvailableSemaphores = _imageAvailableSemaphores;
            Semaphore[] renderFinishedSemaphores = _renderFinishedSemaphores;
            SwapchainKHR swapchain = _swapchain;
            IPostProcessingEffect effect = _effect;
            IScalingFilter scalingFilter = _scalingFilter;

            _swapchainImages = null;
            _swapchainImageViews = null;
            _imageAvailableSemaphores = null;
            _renderFinishedSemaphores = null;
            _swapchain = default;
            _effect = null;
            _scalingFilter = null;

            ExceptionDispatchInfo firstException = null;

            CaptureException(() => effect?.Dispose(), ref firstException);
            CaptureException(() => scalingFilter?.Dispose(), ref firstException);

            unsafe
            {
                if (renderFinishedSemaphores != null)
                {
                    for (int i = renderFinishedSemaphores.Length - 1; i >= 0; i--)
                    {
                        Semaphore semaphore = renderFinishedSemaphores[i];

                        if (semaphore.Handle != 0)
                        {
                            CaptureException(() => _gd.Api.DestroySemaphore(_device, semaphore, null), ref firstException);
                        }
                    }
                }

                if (imageAvailableSemaphores != null)
                {
                    for (int i = imageAvailableSemaphores.Length - 1; i >= 0; i--)
                    {
                        Semaphore semaphore = imageAvailableSemaphores[i];

                        if (semaphore.Handle != 0)
                        {
                            CaptureException(() => _gd.Api.DestroySemaphore(_device, semaphore, null), ref firstException);
                        }
                    }
                }
            }

            if (swapchainImageViews != null)
            {
                for (int i = swapchainImageViews.Length - 1; i >= 0; i--)
                {
                    TextureView texture = swapchainImageViews[i];
                    CaptureException(() => texture?.Dispose(), ref firstException);
                }
            }

            if (_gd.SwapchainApi != null && swapchain.Handle != 0)
            {
                unsafe
                {
                    CaptureException(() => _gd.SwapchainApi.DestroySwapchain(_device, swapchain, null), ref firstException);
                }
            }

            firstException?.Throw();
        }

        public override void Dispose()
        {
            Dispose(true);
        }

        private static void CaptureException(Action action, ref ExceptionDispatchInfo firstException)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
