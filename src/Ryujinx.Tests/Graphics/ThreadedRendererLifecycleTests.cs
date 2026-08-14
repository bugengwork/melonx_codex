using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    [Timeout(10000)]
    public class ThreadedRendererLifecycleTests
    {
        private sealed class RecordingRenderer : IRenderer
        {
            private readonly ConcurrentQueue<string> _calls = new();

            public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured
            {
                add { }
                remove { }
            }

            public bool PreferThreading => true;
            public IPipeline Pipeline => null;
            public IWindow Window => null;
            public string[] Calls => _calls.ToArray();
            public Action PreFrameAction { get; set; }
            public Action<ReadOnlyMemory<byte>> SetBufferDataAction { get; set; }
            public Func<BufferHandle, int, int, PinnedSpan<byte>> GetBufferDataAction { get; set; }
            public int CreateSamplerCount;

            public void BackgroundContextAction(Action action, bool alwaysBackground = false) => action();
            public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default) => default;
            public BufferHandle CreateBuffer(nint pointer, int size) => default;
            public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers) => default;
            public IImageArray CreateImageArray(int size, bool isBuffer) => null;
            public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info) => null;
            public ISampler CreateSampler(SamplerCreateInfo info)
            {
                Interlocked.Increment(ref CreateSamplerCount);
                return null;
            }
            public ITexture CreateTexture(TextureCreateInfo info) => null;
            public ITextureArray CreateTextureArray(int size, bool isBuffer) => null;
            public bool PrepareHostMapping(nint address, ulong size) => false;
            public void CreateSync(ulong id, bool strict) { }
            public void DeleteBuffer(BufferHandle buffer) { }
            public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size) =>
                GetBufferDataAction?.Invoke(buffer, offset, size) ?? default;
            public Capabilities GetCapabilities() => default;
            public ulong GetCurrentSync() => 0;
            public HardwareInfo GetHardwareInfo() => default;
            public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info) => null;
            public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data) =>
                SetBufferDataAction?.Invoke(data.ToArray());
            public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved) => null;
            public void ResetCounter(CounterType type) { }
            public void WaitSync(ulong id) { }
            public void Initialize(GraphicsDebugLevel logLevel) { }
            public void SetInterruptAction(Action<Action> interruptAction) { }
            public void Screenshot() { }

            public void PreFrame()
            {
                _calls.Enqueue(nameof(PreFrame));
                PreFrameAction?.Invoke();
            }
            public void UpdateCounters() => _calls.Enqueue(nameof(UpdateCounters));
            public void Dispose() => _calls.Enqueue(nameof(Dispose));
        }

        [Test]
        public void DisposeWithoutRunLoopDrainsCommandsAndIsIdempotent()
        {
            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);

            renderer.PreFrame();
            renderer.UpdateCounters();
            renderer.Dispose();
            renderer.Dispose();

            Assert.That(
                baseRenderer.Calls,
                Is.EqualTo(new[]
                {
                    nameof(RecordingRenderer.PreFrame),
                    nameof(RecordingRenderer.UpdateCounters),
                    nameof(RecordingRenderer.Dispose),
                }));
        }

        [Test]
        public void HealthyCommandProductionDoesNotAllocatePerCommand()
        {
            const int CommandCount = 9000;

            using ManualResetEventSlim backendBlocked = new(false);
            using ManualResetEventSlim releaseBackend = new(false);
            RecordingRenderer baseRenderer = new()
            {
                PreFrameAction = () =>
                {
                    backendBlocked.Set();
                    releaseBackend.Wait();
                },
            };
            ThreadedRenderer renderer = new(baseRenderer);
            long allocatedBytes = -1;
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        // Hold the consumer on one command so the queue remains
                        // non-empty. This excludes the pre-existing event wake-up
                        // allocation and measures only steady-state production.
                        renderer.PreFrame();
                        backendBlocked.Wait();

                        renderer.UpdateCounters();

                        long before = GC.GetAllocatedBytesForCurrentThread();

                        for (int index = 0; index < CommandCount; index++)
                        {
                            renderer.UpdateCounters();
                        }

                        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                        releaseBackend.Set();
                        renderer.RequestStopAfterDrain();
                    });

                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                releaseBackend.Set();
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(backendFailure, Is.Null);
            Assert.That(allocatedBytes, Is.LessThanOrEqualTo(2048));
        }

        [Test]
        public void StopDrainsProducerAndTeardownCommandsBeforeBaseDispose()
        {
            const int ProducerCommandCount = 8;
            const int TeardownCommandCount = 5;

            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);
            using ManualResetEventSlim producerFinished = new(false);
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        for (int index = 0; index < ProducerCommandCount; index++)
                        {
                            renderer.PreFrame();
                        }

                        producerFinished.Set();
                    });

                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(producerFinished.Wait(TimeSpan.FromSeconds(5)), Is.True);

                for (int index = 0; index < TeardownCommandCount; index++)
                {
                    renderer.UpdateCounters();
                }

                renderer.RequestStopAfterDrain();
                renderer.RequestStopAfterDrain();

                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(backendFailure, Is.Null);

            string[] calls = baseRenderer.Calls;

            Assert.That(calls.Count(call => call == nameof(RecordingRenderer.PreFrame)), Is.EqualTo(ProducerCommandCount));
            Assert.That(calls.Count(call => call == nameof(RecordingRenderer.UpdateCounters)), Is.EqualTo(TeardownCommandCount));
            Assert.That(calls.Count(call => call == nameof(RecordingRenderer.Dispose)), Is.EqualTo(1));
            Assert.That(calls[^1], Is.EqualTo(nameof(RecordingRenderer.Dispose)));
        }

        [Test]
        public void ProducerFaultStopsConsumerAndIsPropagated()
        {
            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);
            InvalidOperationException producerFault = new("producer fault");
            using ManualResetEventSlim producerEntered = new(false);
            using ManualResetEventSlim releaseProducer = new(false);
            Exception runLoopFailure = null;
            Exception disposeFailure = null;
            int initialGpuThreadCount = ThreadedRenderer.ActiveGpuThreadCount;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        producerEntered.Set();
                        releaseProducer.Wait();
                        throw producerFault;
                    });
                }
                catch (Exception exception)
                {
                    runLoopFailure = exception;
                }

                try
                {
                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(producerEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(ThreadedRenderer.ActiveGpuThreadCount, Is.EqualTo(initialGpuThreadCount + 1));

                releaseProducer.Set();

                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                releaseProducer.Set();
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(runLoopFailure, Is.SameAs(producerFault));
            Assert.That(disposeFailure, Is.Null);
            Assert.That(ThreadedRenderer.ActiveGpuThreadCount, Is.EqualTo(initialGpuThreadCount));
            Assert.That(baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.Dispose)), Is.EqualTo(1));

            Assert.DoesNotThrow(renderer.Dispose);
        }

        [Test]
        public void ConsumerFaultWakesWaitingProducerAndIsPropagated()
        {
            InvalidOperationException consumerFault = new("consumer fault");
            RecordingRenderer baseRenderer = new()
            {
                PreFrameAction = () => throw consumerFault,
            };
            ThreadedRenderer renderer = new(baseRenderer);
            Exception runLoopFailure = null;
            Exception teardownProductionFailure = null;
            Exception disposeFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        renderer.PreFrame();

                        // This synchronous command must be woken when the preceding
                        // backend command faults instead of waiting indefinitely.
                        renderer.GetCapabilities();
                    });
                }
                catch (Exception exception)
                {
                    runLoopFailure = exception;
                }

                try
                {
                    // A recorded renderer fault must not block backend-owned
                    // resource-release commands produced during teardown.
                    renderer.UpdateCounters();
                }
                catch (Exception exception)
                {
                    teardownProductionFailure = exception;
                }

                try
                {
                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(runLoopFailure, Is.SameAs(consumerFault));
            Assert.That(teardownProductionFailure, Is.Null);
            Assert.That(disposeFailure, Is.Null);
            Assert.That(baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.UpdateCounters)), Is.EqualTo(1));
            Assert.That(baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.Dispose)), Is.EqualTo(1));
        }

        [Test]
        public void DisposeContinuesDrainingAfterMultipleConsumerFaults()
        {
            const int FaultingCommandCount = 5;
            InvalidOperationException consumerFault = new("consumer fault");
            RecordingRenderer baseRenderer = new()
            {
                PreFrameAction = () => throw consumerFault,
            };
            ThreadedRenderer renderer = new(baseRenderer);

            for (int index = 0; index < FaultingCommandCount; index++)
            {
                renderer.PreFrame();
            }

            Assert.That(Assert.Throws<InvalidOperationException>(renderer.Dispose), Is.SameAs(consumerFault));
            Assert.That(
                baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.PreFrame)),
                Is.EqualTo(FaultingCommandCount));
            Assert.That(baseRenderer.Calls[^1], Is.EqualTo(nameof(RecordingRenderer.Dispose)));
        }

        [Test]
        public void FaultingSpanCommandReleasesSpanBeforeDisposeDrainContinues()
        {
            InvalidOperationException setDataFault = new("set data fault");
            ConcurrentQueue<byte[]> receivedData = new();
            int setDataCallCount = 0;
            RecordingRenderer baseRenderer = new()
            {
                SetBufferDataAction = data =>
                {
                    receivedData.Enqueue(data.ToArray());

                    if (Interlocked.Increment(ref setDataCallCount) == 1)
                    {
                        throw setDataFault;
                    }
                },
            };
            ThreadedRenderer renderer = new(baseRenderer);
            BufferHandle buffer = renderer.CreateBuffer(4, BufferAccess.Default);

            renderer.SetBufferData(buffer, 0, new byte[] { 1, 2 });
            renderer.SetBufferData(buffer, 0, new byte[] { 3, 4 });

            Assert.That(Assert.Throws<InvalidOperationException>(renderer.Dispose), Is.SameAs(setDataFault));
            Assert.That(
                receivedData.ToArray(),
                Is.EqualTo(new[]
                {
                    new byte[] { 1, 2 },
                    new byte[] { 3, 4 },
                }));
            Assert.That(baseRenderer.Calls[^1], Is.EqualTo(nameof(RecordingRenderer.Dispose)));
        }

        [Test]
        public void FaultingResultCommandConsumesAllTableRefsBeforeTeardownCommands()
        {
            InvalidOperationException getDataFault = new("get data fault");
            RecordingRenderer baseRenderer = new()
            {
                GetBufferDataAction = (_, _, _) => throw getDataFault,
            };
            ThreadedRenderer renderer = new(baseRenderer);
            Exception runLoopFailure = null;
            Exception teardownFailure = null;
            Exception disposeFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        BufferHandle buffer = renderer.CreateBuffer(4, BufferAccess.Default);
                        renderer.GetBufferData(buffer, 0, 4);
                    });
                }
                catch (Exception exception)
                {
                    runLoopFailure = exception;
                }

                try
                {
                    renderer.CreateSampler(default);
                }
                catch (Exception exception)
                {
                    teardownFailure = exception;
                }

                try
                {
                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(runLoopFailure, Is.SameAs(getDataFault));
            Assert.That(teardownFailure, Is.Null);
            Assert.That(disposeFailure, Is.Null);
            Assert.That(baseRenderer.CreateSamplerCount, Is.EqualTo(1));
            Assert.That(baseRenderer.Calls[^1], Is.EqualTo(nameof(RecordingRenderer.Dispose)));
        }

        [Test]
        public void SyncWaitObservesConsumerFaultInsteadOfDeadlockingProducer()
        {
            InvalidOperationException consumerFault = new("consumer fault before sync");
            using ManualResetEventSlim faultingCommandEntered = new(false);
            using ManualResetEventSlim releaseFaultingCommand = new(false);
            RecordingRenderer baseRenderer = new()
            {
                PreFrameAction = () =>
                {
                    faultingCommandEntered.Set();
                    releaseFaultingCommand.Wait();
                    throw consumerFault;
                },
            };
            ThreadedRenderer renderer = new(baseRenderer);
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        renderer.PreFrame();
                        renderer.CreateSync(7, false);
                        renderer.WaitSync(7);
                    });
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }

                renderer.Dispose();
            });

            backendThread.Start();

            try
            {
                Assert.That(faultingCommandEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                releaseFaultingCommand.Set();
                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                releaseFaultingCommand.Set();
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(backendFailure, Is.SameAs(consumerFault));
            Assert.That(baseRenderer.Calls[^1], Is.EqualTo(nameof(RecordingRenderer.Dispose)));
        }

        [Test]
        public void WrongThreadDisposeDoesNotConsumeDisposeGate()
        {
            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);
            using ManualResetEventSlim producerEntered = new(false);
            using ManualResetEventSlim releaseProducer = new(false);
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        producerEntered.Set();
                        releaseProducer.Wait();
                    });

                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(producerEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.Throws<InvalidOperationException>(renderer.Dispose);

                releaseProducer.Set();
                renderer.RequestStopAfterDrain();

                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                releaseProducer.Set();
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(backendFailure, Is.Null);
            Assert.That(baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.Dispose)), Is.EqualTo(1));
        }

        [Test]
        public void JoinedBackendAllowsTeardownOwnershipTransfer()
        {
            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(renderer.RequestStopAfterDrain);
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }
            });

            backendThread.Start();
            Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(backendFailure, Is.Null);

            renderer.TakeBackendThreadOwnership();
            renderer.UpdateCounters();
            renderer.Dispose();

            Assert.That(
                baseRenderer.Calls,
                Is.EqualTo(new[]
                {
                    nameof(RecordingRenderer.UpdateCounters),
                    nameof(RecordingRenderer.Dispose),
                }));
        }

        [Test]
        public void TeardownOwnershipTransfersBetweenLiveHostThreads()
        {
            RecordingRenderer baseRenderer = new();
            ThreadedRenderer renderer = new(baseRenderer);
            using ManualResetEventSlim firstOwnerFinished = new(false);

            Thread backendThread = CreateBackgroundThread(
                () => renderer.RunLoop(renderer.RequestStopAfterDrain));
            backendThread.Start();
            Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);

            Thread firstOwnerThread = CreateBackgroundThread(() =>
            {
                renderer.TakeBackendThreadOwnership();
                renderer.UpdateCounters();
                firstOwnerFinished.Set();
            });
            firstOwnerThread.Start();

            Assert.That(firstOwnerFinished.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(firstOwnerThread.Join(TimeSpan.FromSeconds(5)), Is.True);

            renderer.TakeBackendThreadOwnership();
            renderer.PreFrame();
            renderer.Dispose();

            Assert.That(
                baseRenderer.Calls,
                Is.EqualTo(new[]
                {
                    nameof(RecordingRenderer.UpdateCounters),
                    nameof(RecordingRenderer.PreFrame),
                    nameof(RecordingRenderer.Dispose),
                }));
        }

        [Test]
        public void StopWhileQueueIsFullCancelsProducerWithoutDeadlock()
        {
            const int CommandsBeyondQueueCapacity = 10100;

            using ManualResetEventSlim backendCommandEntered = new(false);
            using ManualResetEventSlim releaseBackendCommand = new(false);
            int backendCommandCount = 0;
            int producerAttempts = 0;

            RecordingRenderer baseRenderer = new()
            {
                PreFrameAction = () =>
                {
                    if (Interlocked.Increment(ref backendCommandCount) == 1)
                    {
                        backendCommandEntered.Set();
                        releaseBackendCommand.Wait();
                    }
                },
            };
            ThreadedRenderer renderer = new(baseRenderer);
            Exception backendFailure = null;

            Thread backendThread = CreateBackgroundThread(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        for (int index = 0; index < CommandsBeyondQueueCapacity; index++)
                        {
                            Volatile.Write(ref producerAttempts, index + 1);
                            renderer.PreFrame();
                        }
                    });

                    renderer.Dispose();
                }
                catch (Exception exception)
                {
                    backendFailure = exception;
                }
            });

            backendThread.Start();

            try
            {
                Assert.That(backendCommandEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => Volatile.Read(ref producerAttempts) >= 10000,
                        TimeSpan.FromSeconds(5)),
                    Is.True);

                renderer.RequestStopAfterDrain();
                releaseBackendCommand.Set();

                Assert.That(backendThread.Join(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                releaseBackendCommand.Set();
                renderer.RequestStopAfterDrain();
                backendThread.Join(TimeSpan.FromSeconds(1));
            }

            Assert.That(backendFailure, Is.Null);
            Assert.That(baseRenderer.Calls.Count(call => call == nameof(RecordingRenderer.Dispose)), Is.EqualTo(1));
        }

        private static Thread CreateBackgroundThread(ThreadStart start)
        {
            return new Thread(start)
            {
                IsBackground = true,
            };
        }
    }
}
