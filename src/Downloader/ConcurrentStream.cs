﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader
{
    public class ConcurrentStream : IDisposable
    {
        private ConcurrentPacketBuffer<Packet> _inputBuffer;
        private volatile bool _disposed;
        private Stream _stream;
        private string _path;
        private Task _watcher;
        private CancellationTokenSource _watcherCancelSource;

        public string Path
        {
            get => _path;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _path = value;
                    _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
            }
        }
        public byte[] Data
        {
            get
            {
                Flush();
                if (_stream is MemoryStream mem)
                    return mem.ToArray();

                return null;
            }
            set
            {
                if (value != null)
                {
                    // Don't pass straight value to MemoryStream,
                    // because causes stream to be an immutable array
                    _stream = new MemoryStream(); 
                    _stream.Write(value, 0, value.Length);
                }
            }
        }
        public bool CanRead => _stream?.CanRead == true;
        public bool CanSeek => _stream?.CanSeek == true;
        public bool CanWrite => _stream?.CanWrite == true;
        public long Length => _stream?.Length ?? 0;
        public long Position
        {
            get => _stream?.Position ?? 0;
            set => _stream.Position = value;
        }
        public long MaxMemoryBufferBytes
        {
            get => _inputBuffer.BufferSize;
            set => _inputBuffer.BufferSize = value;
        }

        public ConcurrentStream(Stream stream, long maxMemoryBufferBytes = 0)
        {
            _stream = stream;
            Initial(maxMemoryBufferBytes);
        }

        public ConcurrentStream(string filename, long initSize, long maxMemoryBufferBytes = 0)
        {
            _path = filename;
            _stream = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            if (initSize > 0)
                SetLength(initSize);

            Initial(maxMemoryBufferBytes);
        }

        // parameterless constructor for deserialization
        public ConcurrentStream()
        {
            _stream = new MemoryStream();
            Initial();
        }

        private void Initial(long maxMemoryBufferBytes = 0)
        {
            _inputBuffer = new ConcurrentPacketBuffer<Packet>(maxMemoryBufferBytes);
            _watcherCancelSource = new CancellationTokenSource();

            Task<Task> task = Task.Factory.StartNew(
                function: Watcher,
                cancellationToken: _watcherCancelSource.Token,
                creationOptions: TaskCreationOptions.LongRunning,
                scheduler: TaskScheduler.Default);

            _watcher = task.Unwrap();
        }

        public Stream OpenRead()
        {
            Flush();
            Seek(0, SeekOrigin.Begin);
            return _stream;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var stream = OpenRead();
            return stream.Read(buffer, offset, count);
        }

        public async Task WriteAsync(long position, byte[] bytes, int length)
        {
            await _inputBuffer.TryAdd(new Packet(position, bytes, length));
        }

        private async Task Watcher()
        {
            try
            {
                while (!_watcherCancelSource.IsCancellationRequested)
                {
                    // Warning 1: When the Watcher() is here, at the same time Flush() is called,
                    // then the flush method checks if the queue is not empty,
                    // wait until will is empty. But, after checking the queue is not empty,
                    // immediately the below code is executed, and the last packet is consumed.
                    // So the last wait becomes deadlock!
                    var packet = await _inputBuffer.WaitTryTakeAsync(_watcherCancelSource.Token).ConfigureAwait(false);
                    if (packet != null)
                    {
                        // Warning 2: When the Watcher() is here, at the same time Flush() is called,
                        // then the Flush method checks if the queue is empty, so break workflow.
                        // but, the stream is not still filled!

                        await WritePacketOnFile(packet).ConfigureAwait(false);
                    }
                    // After last packet writing completion, we can check to continue adding
                    _inputBuffer.ResumeAddingIfEmpty();
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
            {
                await Task.Yield();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                _watcherCancelSource.Cancel(true);
            }
        }

        public long Seek(long offset, SeekOrigin origin)
        {
            if (offset != Position && CanSeek)
            {
                _stream.Seek(offset, origin);
            }

            return Position;
        }

        public void SetLength(long value)
        {
            _stream.SetLength(value);
        }

        private async Task WritePacketOnFile(Packet packet)
        {
            // seek with SeekOrigin.Begin is so faster than SeekOrigin.Current
            Seek(packet.Position, SeekOrigin.Begin);
            await _stream.WriteAsync(packet.Data, 0, packet.Length).ConfigureAwait(false);
            packet.Dispose();
        }

        public void Flush()
        {
            _inputBuffer.WaitToComplete();

            if (_stream?.CanRead == true)
            {
                _stream?.Flush();
            }

            GC.Collect();
        }

        public void Dispose()
        {
            if (_disposed == false)
            {
                _disposed = true;
                Flush();
                _watcherCancelSource.Cancel(false); // request the cancellation
                _watcher.Wait();
                _watcher.Dispose();
                _stream.Dispose();
                _inputBuffer.Dispose();
            }
        }
    }
}
