using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KPreisser.LockTests
{
    [TestClass]
    public class AsyncReaderWriterLockSlimTests
    {
        public AsyncReaderWriterLockSlimTests()
            : base()
        {
        }


        [TestMethod]
        public void CanEnterLocksSync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            Assert.IsTrue(myLock.TryEnterReadLock(0));
            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            myLock.ExitReadLock();
            myLock.ExitReadLock();

            myLock.EnterWriteLock();
            Assert.IsFalse(myLock.TryEnterReadLock(0));
            Assert.IsFalse(myLock.TryEnterWriteLock(0));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        public async Task CanEnterLocksAync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            await myLock.EnterReadLockAsync();
            Assert.IsTrue(await myLock.TryEnterReadLockAsync(0));
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(0));
            myLock.ExitReadLock();
            myLock.ExitReadLock();

            await myLock.EnterWriteLockAsync();
            Assert.IsFalse(await myLock.TryEnterReadLockAsync(0));
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(0));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void ThrowsOnIncorrectReadLockRelease()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            myLock.EnterReadLock();

            myLock.ExitReadLock();
            myLock.ExitReadLock();
            myLock.ExitReadLock(); // should throw
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void ThrowsOnIncorrectWriteLockRelease()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterWriteLock();

            myLock.ExitWriteLock();
            myLock.ExitWriteLock(); // should throw
        }

        [TestMethod]
        public async Task CheckMixedSyncAsync()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            await myLock.EnterReadLockAsync();

            myLock.ExitReadLock();
            myLock.ExitReadLock();

            myLock.EnterWriteLock();
            Assert.IsFalse(await myLock.TryEnterWriteLockAsync(10));
            myLock.ExitWriteLock();
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public void ThrowsOperationCanceledException()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            // Should throw without entering the lock.
            myLock.EnterReadLock(new CancellationToken(true));
        }

        [TestMethod]
        public void CheckMultipleThreads()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            myLock.EnterReadLock();
            bool enteredWriteLock = false;
            var t1 = new Thread(() =>
            {
                myLock.EnterWriteLock();
                Volatile.Write(ref enteredWriteLock, true);
                myLock.ExitWriteLock();
            });
            t1.Start();

            // Wait a bit, then release the read lock, so that the other thread can enter
            // the write lock.
            Thread.Sleep(200);
            Assert.IsFalse(Volatile.Read(ref enteredWriteLock));
            myLock.ExitReadLock();

            t1.Join();
            Assert.IsTrue(Volatile.Read(ref enteredWriteLock));
        }

        [TestMethod]
        public void CheckMultipleThreadsAndTasks()
        {
            var myLock = new AsyncReaderWriterLockSlim();
            int readLocksEntered = 0;

            var threads = new Thread[5];
            var tasks = new Task[5];

            myLock.EnterWriteLock();
            for (int i = 0; i < threads.Length; i++)
            {
                (threads[i] = new Thread(() =>
                {
                    myLock.EnterReadLock();
                    Interlocked.Increment(ref readLocksEntered);
                })).Start();
            }
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    await myLock.EnterReadLockAsync();
                    Interlocked.Increment(ref readLocksEntered);
                });
            }

            // Wait a bit, then release the read lock, so that the other thread can enter
            // the write lock.
            Thread.Sleep(200);
            Assert.AreEqual(0, Volatile.Read(ref readLocksEntered));
            myLock.ExitWriteLock();

            // Wait for the tasks and threads.
            foreach (var thread in threads)
                thread.Join();
            foreach (var task in tasks)
                task.GetAwaiter().GetResult();

            Assert.AreEqual(threads.Length + tasks.Length, Volatile.Read(ref readLocksEntered));
        }

        [TestMethod]
        [Timeout(5000)]
        public void WaitingReaderIsReleasedAfterWaitingWriterCanceled()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            // Thread A enters a read lock.
            myLock.EnterReadLock();

            // Thread B wants to enter a write lock within 2 seconds. Because Thread A holds
            // a read lock, Thread B will not get the write lock.
            bool threadBResult = true;
            var threadB = new Thread(() =>
            {
                bool result = myLock.TryEnterWriteLock(600);
                Volatile.Write(ref threadBResult, result);
            });
            threadB.Start();

            // Wait a bit before starting the next thread, to ensure Thread B is already
            // in the TryEnter...() call.
            Thread.Sleep(200);

            // Thread C wants to enter a read lock. It should get the lock after
            // 2 seconds because Thread B cancels its try to get the write lock after
            // that time.
            var threadC = new Thread(() =>
            {
                myLock.EnterReadLock();
            });
            threadC.Start();

            threadB.Join();
            threadC.Join();

            Assert.IsFalse(Volatile.Read(ref threadBResult));

            myLock.EnterReadLock();
        }

        [TestMethod]
        public void LoadTest()
        {
            var myLock = new AsyncReaderWriterLockSlim();

            object lockCountSyncRoot = new object();
            int readLockCount = 0, writeLockCount = 0;

            bool incorrectLockCount = false;

            void checkLockCount()
            {
                Debug.WriteLine($"ReadLocks = {readLockCount}, WriteLocks = {writeLockCount}");

                bool countIsCorrect = readLockCount == 0 && writeLockCount == 0 ||
                        readLockCount > 0 && writeLockCount == 0 ||
                        readLockCount == 0 && writeLockCount == 1;

                if (!countIsCorrect)
                    Volatile.Write(ref incorrectLockCount, true);
            }

            bool cancel = false;

            var threads = new Thread[20];
            var tasks = new Task[20];

            var masterRandom = new Random();

            for (int i = 0; i < threads.Length; i++)
            {
                var random = new Random(masterRandom.Next());
                (threads[i] = new Thread(() =>
                {
                    bool isRead = random.Next(100) < 70;
                    if (isRead)
                        myLock.EnterReadLock();
                    else
                        myLock.EnterWriteLock();

                    lock (lockCountSyncRoot)
                    {
                        if (isRead)
                            readLockCount++;
                        else
                            writeLockCount++;
                    }

                    // Simulate work.
                    Thread.Sleep(10);

                    lock (lockCountSyncRoot)
                    {
                        if (isRead)
                        {
                            myLock.ExitReadLock();
                            readLockCount--;
                        }
                        else
                        {
                            myLock.ExitWriteLock();
                            writeLockCount--;
                        }
                    }
                })).Start();
            }
            for (int i = 0; i < tasks.Length; i++)
            {
                var random = new Random(masterRandom.Next());
                tasks[i] = Task.Run(async () =>
                {
                    while (!Volatile.Read(ref cancel))
                    {
                        bool isRead = random.Next(10) < 7;
                        if (isRead)
                            await myLock.EnterReadLockAsync();
                        else
                            await myLock.EnterWriteLockAsync();

                        lock (lockCountSyncRoot)
                        {
                            if (isRead)
                                readLockCount++;
                            else
                                writeLockCount++;

                            checkLockCount();
                        }

                        // Simulate work.
                        await Task.Delay(10);

                        lock (lockCountSyncRoot)
                        {
                            if (isRead)
                            {
                                myLock.ExitReadLock();
                                readLockCount--;
                            }
                            else
                            {
                                myLock.ExitWriteLock();
                                writeLockCount--;
                            }

                            checkLockCount();
                        }
                    }
                });
            }

            // Run for 5 seconds, then stop the tasks and threads.
            Thread.Sleep(5000);

            Volatile.Write(ref cancel, true);
            foreach (var thread in threads)
                thread.Join();
            foreach (var task in tasks)
                task.GetAwaiter().GetResult();

            Assert.IsFalse(incorrectLockCount);
        }
    }
}