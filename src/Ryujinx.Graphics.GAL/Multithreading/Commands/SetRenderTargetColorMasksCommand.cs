using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetRenderTargetColorMasksCommand : IGALCommand, IGALCommand<SetRenderTargetColorMasksCommand>
    {
        public readonly CommandType CommandType => CommandType.SetRenderTargetColorMasks;
        private SpanRef<uint> _componentMask;

        public void Set(SpanRef<uint> componentMask)
        {
            _componentMask = componentMask;
        }

        public static void Run(ref SetRenderTargetColorMasksCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            ReadOnlySpan<uint> componentMask = command._componentMask.Get(threaded);

            try
            {
                renderer.Pipeline.SetRenderTargetColorMasks(componentMask);
            }
            finally
            {
                command._componentMask.Dispose(threaded);
            }
        }
    }
}
