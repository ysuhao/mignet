using System;
using System.IO;
using System.Media;
using System.Threading;

namespace DesktopStamp.Audio
{
    /// <summary>
    /// Synthesizes tactile mechanical click and damped low-frequency paper impact audio
    /// directly in memory as 16-bit PCM WAV data with zero external audio asset dependencies.
    /// </summary>
    public static class StampAudioSynthesizer
    {
        private static readonly byte[] ImpactWavBytes = GenerateStampImpactWav();
        private static readonly byte[] DetentWavBytes = GenerateSoftDetentWav();

        public static byte[] GetImpactWavBytes() => ImpactWavBytes;

        public static void PlayStampImpactAsync()
        {
            PlayWavBufferAsync(ImpactWavBytes);
        }

        public static void PlaySoftDetentAsync()
        {
            PlayWavBufferAsync(DetentWavBytes);
        }

        private static void PlayWavBufferAsync(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length == 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var ms = new MemoryStream(wavBytes, writable: false))
                    using (var player = new SoundPlayer(ms))
                    {
                        player.PlaySync();
                    }
                }
                catch
                {
                    // Fail silently on systems without an active audio output device
                }
            });
        }

        private static byte[] GenerateStampImpactWav()
        {
            const int sampleRate = 44100;
            const double durationSeconds = 0.165;
            int numSamples = (int)(sampleRate * durationSeconds);
            short[] samples = new short[numSamples];

            var rng = new Random(1337);
            double phaseLow = 0.0;
            double phaseBody = 0.0;
            double phaseClick = 0.0;

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;

                // 1) Initial mechanical spring-sleeve click transient (0 .. 12 ms)
                double clickEnv = t < 0.012 ? Math.Exp(-t * 360.0) : 0.0;
                double clickFreq = 1450.0 * Math.Exp(-t * 140.0) + 420.0;
                phaseClick += 2.0 * Math.PI * clickFreq / sampleRate;
                double clickSignal = Math.Sin(phaseClick) * clickEnv * 0.32;

                // 2) Main paper/desk low-frequency impact thud (starts at t = 6 ms)
                double thudSignal = 0.0;
                double paperNoise = 0.0;
                if (t >= 0.006)
                {
                    double td = t - 0.006;
                    // Fast attack (2 ms), exponential decay
                    double attack = Math.Min(1.0, td / 0.0022);
                    double thudEnv = attack * Math.Exp(-td * 34.0);

                    // Pitch drops from 128 Hz down to 54 Hz as impact energy settles into desk surface
                    double lowFreq = 54.0 + 74.0 * Math.Exp(-td * 65.0);
                    double bodyFreq = lowFreq * 2.15;

                    phaseLow += 2.0 * Math.PI * lowFreq / sampleRate;
                    phaseBody += 2.0 * Math.PI * bodyFreq / sampleRate;

                    thudSignal = (Math.Sin(phaseLow) * 0.72 + Math.Sin(phaseBody) * 0.24) * thudEnv;

                    // Brief damped paper contact texture (6 ms .. 26 ms)
                    if (td < 0.022)
                    {
                        double noiseEnv = Math.Exp(-td * 190.0);
                        paperNoise = (rng.NextDouble() * 2.0 - 1.0) * noiseEnv * 0.22;
                    }
                }

                double combined = (clickSignal + thudSignal + paperNoise) * 0.85;
                combined = Math.Max(-0.98, Math.Min(0.98, combined));
                samples[i] = (short)Math.Round(combined * short.MaxValue);
            }

            return EncodePcm16MonoWav(samples, sampleRate);
        }

        private static byte[] GenerateSoftDetentWav()
        {
            const int sampleRate = 44100;
            const double durationSeconds = 0.035;
            int numSamples = (int)(sampleRate * durationSeconds);
            short[] samples = new short[numSamples];

            double phase = 0.0;
            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;
                double env = Math.Exp(-t * 240.0);
                double freq = 520.0 + 380.0 * Math.Exp(-t * 180.0);
                phase += 2.0 * Math.PI * freq / sampleRate;
                double val = Math.Sin(phase) * env * 0.24;
                samples[i] = (short)Math.Round(val * short.MaxValue);
            }

            return EncodePcm16MonoWav(samples, sampleRate);
        }

        private static byte[] EncodePcm16MonoWav(short[] samples, int sampleRate)
        {
            int dataByteCount = samples.Length * 2;
            using (var ms = new MemoryStream(44 + dataByteCount))
            using (var bw = new BinaryWriter(ms))
            {
                // RIFF header
                bw.Write(new[] { 'R', 'I', 'F', 'F' });
                bw.Write(36 + dataByteCount);
                bw.Write(new[] { 'W', 'A', 'V', 'E' });

                // fmt subchunk
                bw.Write(new[] { 'f', 'm', 't', ' ' });
                bw.Write(16); // PCM chunk size
                bw.Write((short)1); // AudioFormat = 1 (PCM)
                bw.Write((short)1); // NumChannels = 1 (Mono)
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2); // ByteRate
                bw.Write((short)2); // BlockAlign
                bw.Write((short)16); // BitsPerSample

                // data subchunk
                bw.Write(new[] { 'd', 'a', 't', 'a' });
                bw.Write(dataByteCount);

                for (int i = 0; i < samples.Length; i++)
                {
                    bw.Write(samples[i]);
                }

                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}
