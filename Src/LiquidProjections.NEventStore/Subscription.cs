﻿using LiquidProjections.NEventStore.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidProjections.NEventStore
{
    internal sealed class Subscription : IDisposable
    {
        private readonly NEventStoreAdapter eventStoreClient;
        private CancellationTokenSource cancellationTokenSource;
        private readonly object syncRoot = new object();
        private bool isDisposed;
        private long? lastCheckpoint;
        private readonly Func<IReadOnlyList<Transaction>, Task> observer;

        public Subscription(NEventStoreAdapter eventStoreClient, long? checkpoint,
            Func<IReadOnlyList<Transaction>, Task> observer)
        {
            this.eventStoreClient = eventStoreClient;
            lastCheckpoint = checkpoint;
            this.observer = observer;
        }

        public Task Task { get; private set; }

        public void Start()
        {
            if (Task != null)
            {
                throw new InvalidOperationException("Already started.");
            }

            lock (syncRoot)
            {
                if (isDisposed)
                {
                    throw new ObjectDisposedException(nameof(Subscription));
                }

                cancellationTokenSource = new CancellationTokenSource();

                Task = Task.Factory
                    .StartNew(
                        RunAsync,
                        cancellationTokenSource.Token,
                        TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        public void Complete()
        {
            Dispose();
        }

        public void Dispose()
        {
            bool isDisposing;

            lock (syncRoot)
            {
                isDisposing = !isDisposed;

                if (isDisposing)
                {
                    isDisposed = true;
                }
            }

            if (isDisposing)
            {
                if (cancellationTokenSource != null)
                {
                    if (!cancellationTokenSource.IsCancellationRequested)
                    {
                        cancellationTokenSource.Cancel();
                    }

                    Task?.Wait();
                    cancellationTokenSource.Dispose();
                }

                lock (eventStoreClient.subscriptionLock)
                {
                    eventStoreClient.subscriptions.Remove(this);
                }
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    Page page = await eventStoreClient.GetNextPage(lastCheckpoint)
                        .WithWaitCancellation(cancellationTokenSource.Token)
                        .ConfigureAwait(false);

                    await observer(page.Transactions).ConfigureAwait(false);
                    lastCheckpoint = page.LastCheckpoint;
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing.
            }
            catch (Exception exception)
            {
                LogProvider.GetCurrentClassLogger().FatalException(
                    "NEventStore polling task has failed. Event subscription has been cancelled.",
                    exception);
            }
        }
    }
}