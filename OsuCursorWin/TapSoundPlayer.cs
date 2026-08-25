using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace OsuCursorWin;

internal sealed class TapSoundPlayer : IDisposable
{
    private const int PlayerCount = 12;

    private readonly WaveOutEvent[] _outputs = new WaveOutEvent[PlayerCount];
    private readonly WaveStream?[] _streams = new WaveStream?[PlayerCount];
    private readonly byte[] _pcm;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _bitsPerSample;
    private readonly BlockingCollection<PlayRequest> _queue = new(new ConcurrentQueue<PlayRequest>());
    private readonly Thread _worker;
    private int _nextPlayer;

    internal bool Enabled { get; set; }

    internal TapSoundPlayer(byte[] wavBytes)
    {
        ParseWave(wavBytes, out _pcm, out _channels, out _sampleRate, out _bitsPerSample);

        for (var i = 0; i < _outputs.Length; i++)
        {
            _outputs[i] = new WaveOutEvent
            {
                DesiredLatency = 60,
                NumberOfBuffers = 3
            };
        }

        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "OsuCursorAudio"
        };
        _worker.Start();
    }

    internal void Play(double frequency, double volume, double balance)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            _queue.Add(new PlayRequest(frequency, volume, balance));
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to enqueue tap sound: {ex}");
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join();

        foreach (var output in _outputs)
        {
            output?.Stop();
            output?.Dispose();
        }

        foreach (var stream in _streams)
        {
            stream?.Dispose();
        }

        _queue.Dispose();
    }

    private void ProcessQueue()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            try
            {
                PlayCore(request);
            }
            catch (Exception ex)
            {
                Program.Log($"Failed to play queued sound: {ex}");
            }
        }
    }

    private void PlayCore(PlayRequest request)
    {
        var outputRate = Math.Clamp((int)Math.Round(_sampleRate * request.Frequency), 8000, 96000);
        var wav = BuildWave(outputRate, request.Volume, request.Balance);

        // Use a single, consistent slot index so the stream and its owning output
        // stay paired. Previously the slot was advanced before disposing/assigning
        // the stream, which (a) left the previous stream of the used slot alive
        // forever (a growing WAV-buffer leak) and (b) misassociated each output with
        // the stream stored in the *next* slot.
        var slot = _nextPlayer;
        _nextPlayer = (slot + 1) % _outputs.Length;

        var output = _outputs[slot];
        output.Stop();
        _streams[slot]?.Dispose();

        var stream = new WaveFileReader(new MemoryStream(wav));
        _streams[slot] = stream;
        output.Init(stream);
        output.Play();
    }

    private readonly record struct PlayRequest(double Frequency, double Volume, double Balance);

    private byte[] BuildWave(int outputRate, double volume, double balance)
    {
        var bytesPerSample = _bitsPerSample / 8;
        var frameCount = _pcm.Length / (bytesPerSample * _channels);
        var output = new byte[_pcm.Length];

        var pan = Math.Clamp(balance, -1.0, 1.0);
        var leftGain = volume * Math.Sqrt(Math.Max(0.0, 1.0 - pan));
        var rightGain = volume * Math.Sqrt(Math.Max(0.0, 1.0 + pan));

        for (var i = 0; i < frameCount; i++)
        {
            var offset = i * bytesPerSample * _channels;

            if (_channels == 2)
            {
                var left = ReadSample(offset);
                var right = ReadSample(offset + bytesPerSample);
                WriteSample(output, offset, left * leftGain);
                WriteSample(output, offset + bytesPerSample, right * rightGain);
            }
            else
            {
                var sample = ReadSample(offset);
                WriteSample(output, offset, sample * volume);
            }
        }

        return BuildWaveFile(output, outputRate, _channels, _bitsPerSample);
    }

    private short ReadSample(int offset)
    {
        return _bitsPerSample switch
        {
            16 => BitConverter.ToInt16(_pcm, offset),
            8 => (short)((_pcm[offset] - 128) * 256),
            _ => throw new NotSupportedException($"Unsupported WAV bit depth: {_bitsPerSample}")
        };
    }

    private static void WriteSample(byte[] output, int offset, double value)
    {
        var sample = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
        BitConverter.GetBytes(sample).CopyTo(output, offset);
    }

    private static byte[] BuildWaveFile(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var bytesPerSample = bitsPerSample / 8;
        var byteRate = sampleRate * channels * bytesPerSample;
        var blockAlign = channels * bytesPerSample;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();
        return stream.ToArray();
    }

    private static void ParseWave(byte[] data, out byte[] pcm, out int channels, out int sampleRate, out int bitsPerSample)
    {
        if (data.Length < 12
            || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a valid RIFF WAVE file.");
        }

        channels = 0;
        sampleRate = 0;
        bitsPerSample = 0;
        pcm = Array.Empty<byte>();

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var chunkSize = BitConverter.ToInt32(data, offset + 4);
            var chunkData = offset + 8;

            if (chunkId == "fmt ")
            {
                var audioFormat = BitConverter.ToInt16(data, chunkData);
                if (audioFormat != 1)
                {
                    throw new NotSupportedException($"Unsupported WAV format: {audioFormat}");
                }

                channels = BitConverter.ToInt16(data, chunkData + 2);
                sampleRate = BitConverter.ToInt32(data, chunkData + 4);
                bitsPerSample = BitConverter.ToInt16(data, chunkData + 14);
            }
            else if (chunkId == "data")
            {
                pcm = new byte[chunkSize];
                Array.Copy(data, chunkData, pcm, 0, chunkSize);
            }

            offset = chunkData + chunkSize + (chunkSize & 1);
        }

        if (channels == 0 || sampleRate == 0 || bitsPerSample == 0 || pcm.Length == 0)
        {
            throw new InvalidDataException("WAV file is missing required chunks.");
        }
    }
}
