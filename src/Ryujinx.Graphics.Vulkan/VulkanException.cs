using Silk.NET.Vulkan;
using System;
using System.Runtime.Serialization;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    public sealed class VulkanRendererInitializationRollbackException : AggregateException
    {
        public VulkanRendererInitializationRollbackException(
            Exception initializationFailure,
            Exception rollbackFailure)
            : base(
                "Vulkan renderer initialization failed and its rollback did not complete.",
                initializationFailure,
                rollbackFailure)
        {
        }
    }

    static class ResultExtensions
    {
        private static long _deviceLostCount;

        public static long DeviceLostCount => Interlocked.Read(ref _deviceLostCount);

        public static bool IsError(this Result result)
        {
            // Only negative result codes are errors.
            return result < Result.Success;
        }

        public static void ThrowOnError(this Result result)
        {
            // Only negative result codes are errors.
            if (result.IsError())
            {
                if (result == Result.ErrorDeviceLost)
                {
                    Interlocked.Increment(ref _deviceLostCount);
                }

                throw new VulkanException(result);
            }
        }
    }

    class VulkanException : Exception
    {
        public VulkanException()
        {
        }

        public VulkanException(Result result) : base($"Unexpected API error \"{result}\".")
        {
        }

        public VulkanException(string message) : base(message)
        {
        }

        public VulkanException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
