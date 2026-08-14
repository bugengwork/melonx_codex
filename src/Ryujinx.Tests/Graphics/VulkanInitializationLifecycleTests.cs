using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Ryujinx.Tests.Graphics
{
    [TestFixture]
    public class VulkanInitializationLifecycleTests
    {
        private static readonly VulkanInitializationStage[] Stages =
        {
            VulkanInitializationStage.Instance,
            VulkanInitializationStage.Surface,
            VulkanInitializationStage.Device,
            VulkanInitializationStage.Swapchain,
        };

        [TestCase((int)VulkanInitializationStage.Instance)]
        [TestCase((int)VulkanInitializationStage.Surface)]
        [TestCase((int)VulkanInitializationStage.Device)]
        [TestCase((int)VulkanInitializationStage.Swapchain)]
        public void FaultAfterStageRollsBackCreatedResourcesOnceInReverseOrder(
            int faultStageValue)
        {
            VulkanInitializationStage faultStage = (VulkanInitializationStage)faultStageValue;
            List<string> events = new();
            Dictionary<VulkanInitializationStage, int> destroyCounts = new();
            InvalidOperationException injectedFailure = new($"fault after {faultStage}");

            VulkanInitializationLifecycle lifecycle = new(stage =>
            {
                if (stage == faultStage)
                {
                    throw injectedFailure;
                }
            });

            Exception thrown = null;

            try
            {
                foreach (VulkanInitializationStage stage in Stages)
                {
                    VulkanInitializationStage capturedStage = stage;

                    lifecycle.BeginStage(capturedStage, () =>
                    {
                        events.Add($"destroy:{capturedStage}");
                        destroyCounts[capturedStage] =
                            destroyCounts.GetValueOrDefault(capturedStage) + 1;
                    });

                    events.Add($"create:{capturedStage}");
                    lifecycle.CompleteStage(capturedStage);
                }
            }
            catch (Exception exception)
            {
                thrown = exception;
            }

            ExceptionDispatchInfo rollbackFailure = null;
            lifecycle.Rollback(ref rollbackFailure);
            lifecycle.Rollback(ref rollbackFailure);

            Assert.That(thrown, Is.SameAs(injectedFailure));
            Assert.That(rollbackFailure, Is.Null);
            Assert.That(lifecycle.CurrentStage, Is.EqualTo(VulkanInitializationStage.None));

            VulkanInitializationStage[] createdStages = Stages
                .TakeWhile(stage => (int)stage <= (int)faultStage)
                .ToArray();

            CollectionAssert.AreEqual(
                createdStages.Select(stage => $"create:{stage}")
                    .Concat(createdStages.Reverse().Select(stage => $"destroy:{stage}")),
                events);

            foreach (VulkanInitializationStage stage in createdStages)
            {
                Assert.That(destroyCounts[stage], Is.EqualTo(1), $"{stage} destroy count");
            }
        }

        [Test]
        public void SwapchainIsReleasedWhenInitializationFailsBeforeStageCompletes()
        {
            int destroyCount = 0;
            VulkanInitializationLifecycle lifecycle = new();

            lifecycle.BeginStage(
                VulkanInitializationStage.Swapchain,
                () => destroyCount++);

            // Mirrors Window being assigned before Window.Initialize throws.
            ExceptionDispatchInfo rollbackFailure = null;
            lifecycle.Rollback(ref rollbackFailure);
            lifecycle.Rollback(ref rollbackFailure);

            Assert.That(rollbackFailure, Is.Null);
            Assert.That(destroyCount, Is.EqualTo(1));
            Assert.That(lifecycle.CurrentStage, Is.EqualTo(VulkanInitializationStage.None));
        }

        [Test]
        public void RollbackContinuesAfterDestroyFailureAndPreservesFirstFailure()
        {
            List<VulkanInitializationStage> destroyed = new();
            InvalidOperationException firstFailure = new("device destroy failed");
            VulkanInitializationLifecycle lifecycle = new();

            foreach (VulkanInitializationStage stage in Stages)
            {
                VulkanInitializationStage capturedStage = stage;

                lifecycle.BeginStage(capturedStage, () =>
                {
                    destroyed.Add(capturedStage);

                    if (capturedStage == VulkanInitializationStage.Device)
                    {
                        throw firstFailure;
                    }
                });

                lifecycle.CompleteStage(capturedStage);
            }

            ExceptionDispatchInfo rollbackFailure = null;
            lifecycle.Rollback(ref rollbackFailure);
            lifecycle.Rollback(ref rollbackFailure);

            CollectionAssert.AreEqual(Stages.Reverse(), destroyed);
            Assert.That(rollbackFailure, Is.Not.Null);
            Assert.That(rollbackFailure.SourceException, Is.SameAs(firstFailure));
        }

        [Test]
        public void NormalDisposeCanReleaseSwapchainThenRollbackRemainingResourcesOnce()
        {
            List<VulkanInitializationStage> destroyed = new();
            VulkanInitializationLifecycle lifecycle = new();

            foreach (VulkanInitializationStage stage in Stages)
            {
                VulkanInitializationStage capturedStage = stage;
                lifecycle.BeginStage(capturedStage, () => destroyed.Add(capturedStage));
                lifecycle.CompleteStage(capturedStage);
            }

            ExceptionDispatchInfo disposeFailure = null;
            lifecycle.ReleaseStage(VulkanInitializationStage.Swapchain, ref disposeFailure);
            lifecycle.Rollback(ref disposeFailure);
            lifecycle.ReleaseStage(VulkanInitializationStage.Swapchain, ref disposeFailure);
            lifecycle.Rollback(ref disposeFailure);

            Assert.That(disposeFailure, Is.Null);
            CollectionAssert.AreEqual(Stages.Reverse(), destroyed);
        }
    }
}
