using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetUniformBuffersCommand : IGALCommand, IGALCommand<SetUniformBuffersCommand>
    {
        public readonly CommandType CommandType => CommandType.SetUniformBuffers;
        private SpanRef<BufferAssignment> _buffers;

        public void Set(SpanRef<BufferAssignment> buffers)
        {
            _buffers = buffers;
        }

        public static void Run(ref SetUniformBuffersCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            Span<BufferAssignment> buffers = command._buffers.Get(threaded);

            try
            {
                renderer.Pipeline.SetUniformBuffers(threaded.Buffers.MapBufferRanges(buffers));
            }
            finally
            {
                command._buffers.Dispose(threaded);
            }
        }
    }
}
