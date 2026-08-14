using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Ryujinx.Graphics.Vulkan
{
    internal enum VulkanInitializationStage
    {
        None,
        Instance,
        Surface,
        Device,
        Swapchain,
    }

    /// <summary>
    /// Tracks the primary Vulkan resources while the renderer is being initialized.
    /// Resources are registered before their initialization work starts so that a
    /// partially-created resource can still participate in rollback.
    /// </summary>
    internal sealed class VulkanInitializationLifecycle
    {
        private sealed class Resource
        {
            public VulkanInitializationStage Stage { get; }
            public Action Release { get; }
            public bool Completed { get; set; }

            public Resource(VulkanInitializationStage stage, Action release)
            {
                Stage = stage;
                Release = release;
            }
        }

        private readonly Stack<Resource> _resources = new();
        private readonly Action<VulkanInitializationStage> _faultInjector;
        private readonly object _lock = new();
        private VulkanInitializationStage _currentStage;

        public VulkanInitializationStage CurrentStage
        {
            get
            {
                lock (_lock)
                {
                    return _currentStage;
                }
            }
        }

        public VulkanInitializationLifecycle(Action<VulkanInitializationStage> faultInjector = null)
        {
            _faultInjector = faultInjector;
        }

        public void BeginStage(VulkanInitializationStage stage, Action release)
        {
            ArgumentNullException.ThrowIfNull(release);

            if (stage == VulkanInitializationStage.None)
            {
                throw new ArgumentOutOfRangeException(nameof(stage));
            }

            lock (_lock)
            {
                if (_resources.Count != 0 && stage <= _resources.Peek().Stage)
                {
                    throw new InvalidOperationException(
                        $"Vulkan initialization stage {stage} cannot follow {_resources.Peek().Stage}.");
                }

                _resources.Push(new Resource(stage, release));
            }
        }

        public void CompleteStage(VulkanInitializationStage stage)
        {
            lock (_lock)
            {
                if (_resources.Count == 0 || _resources.Peek().Stage != stage)
                {
                    throw new InvalidOperationException(
                        $"Vulkan initialization stage {stage} was not registered.");
                }

                Resource resource = _resources.Peek();

                if (resource.Completed)
                {
                    throw new InvalidOperationException(
                        $"Vulkan initialization stage {stage} was already completed.");
                }

                resource.Completed = true;
                _currentStage = stage;
            }

            // Inject only after the resource was successfully created and marked
            // complete. Initialize's catch path will then exercise the real rollback.
            _faultInjector?.Invoke(stage);
        }

        public void ReleaseStage(
            VulkanInitializationStage stage,
            ref ExceptionDispatchInfo firstException)
        {
            Resource resource = null;

            lock (_lock)
            {
                if (_resources.Count != 0 && _resources.Peek().Stage == stage)
                {
                    resource = _resources.Pop();
                    RefreshCurrentStage();
                }
            }

            Release(resource, ref firstException);
        }

        public void Rollback(ref ExceptionDispatchInfo firstException)
        {
            while (true)
            {
                Resource resource;

                lock (_lock)
                {
                    if (_resources.Count == 0)
                    {
                        _currentStage = VulkanInitializationStage.None;
                        return;
                    }

                    resource = _resources.Pop();
                    RefreshCurrentStage();
                }

                // Continue unwinding even when one destroy action fails. The first
                // error is retained so the caller can aggregate it with init failure.
                Release(resource, ref firstException);
            }
        }

        private void RefreshCurrentStage()
        {
            _currentStage = VulkanInitializationStage.None;

            foreach (Resource resource in _resources)
            {
                if (resource.Completed)
                {
                    _currentStage = resource.Stage;
                    break;
                }
            }
        }

        private static void Release(Resource resource, ref ExceptionDispatchInfo firstException)
        {
            if (resource == null)
            {
                return;
            }

            try
            {
                resource.Release();
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
