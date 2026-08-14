using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetScissorsCommand : IGALCommand, IGALCommand<SetScissorsCommand>
    {
        public readonly CommandType CommandType => CommandType.SetScissor;
        private SpanRef<Rectangle<int>> _scissors;

        public void Set(SpanRef<Rectangle<int>> scissors)
        {
            _scissors = scissors;
        }

        public static void Run(ref SetScissorsCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            Span<Rectangle<int>> scissors = command._scissors.Get(threaded);

            try
            {
                renderer.Pipeline.SetScissors(scissors);
            }
            finally
            {
                command._scissors.Dispose(threaded);
            }
        }
    }
}
