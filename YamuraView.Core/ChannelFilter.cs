namespace YamuraView.Core
{
    /// <summary>Display-only smoothing filter type for one channel (see <see cref="ChannelFilter"/>).</summary>
    public enum ChannelFilterType
    {
        None,
        MovingAverage,
        Median,
        SavitzkyGolay,
    }

    /// <summary>A channel's display filter: what kind and how wide. Window is in samples,
    /// centered on each point (so a window of 9 spans 4 points each side); even values are
    /// treated as the next odd size up.</summary>
    public sealed record ChannelFilterSettings(ChannelFilterType Type, int WindowSize);

    /// <summary>
    /// Display-only channel smoothing to knock down vibration noise (accelerometer buzz,
    /// spiky samples). Filters a channel's value sequence without touching the raw data -
    /// the charts apply this when building their cached display series, so alignment,
    /// Delta-T, and anything else computing from raw data are unaffected. Both filters use
    /// a window centered on each sample (truncated at the ends of the run), so the filtered
    /// trace has no time lag relative to the raw one - the cursor still lands on the state
    /// the car was actually in at that moment.
    /// </summary>
    public static class ChannelFilter
    {
        /// <summary>
        /// Returns the filtered copy of <paramref name="values"/> (same length, index-aligned
        /// with the input, which is index-aligned with the channel's timestamps). A null/None
        /// settings or a window under 3 returns the input untouched.
        /// </summary>
        public static IList<float> Apply(IList<float> values, ChannelFilterSettings? settings)
        {
            if (settings == null || settings.Type == ChannelFilterType.None || settings.WindowSize < 3 || values.Count < 3)
            {
                return values;
            }
            int half = settings.WindowSize / 2;
            return settings.Type switch
            {
                ChannelFilterType.MovingAverage => MovingAverage(values, half),
                ChannelFilterType.Median => Median(values, half),
                ChannelFilterType.SavitzkyGolay => SavitzkyGolay(values, half),
                _ => values,
            };
        }

        /// <summary>Centered moving average via prefix sums - O(n) regardless of window.</summary>
        static float[] MovingAverage(IList<float> values, int half)
        {
            int n = values.Count;
            double[] prefix = new double[n + 1];
            for (int i = 0; i < n; i++)
            {
                prefix[i + 1] = prefix[i] + values[i];
            }
            float[] result = new float[n];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - half);
                int hi = Math.Min(n - 1, i + half);
                result[i] = (float)((prefix[hi + 1] - prefix[lo]) / (hi - lo + 1));
            }
            return result;
        }

        /// <summary>Centered median - kills isolated spikes without smearing real edges the
        /// way an average does. O(n * w log w); fine at display-filter window sizes.</summary>
        static float[] Median(IList<float> values, int half)
        {
            int n = values.Count;
            float[] result = new float[n];
            float[] window = new float[2 * half + 1];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - half);
                int hi = Math.Min(n - 1, i + half);
                int count = hi - lo + 1;
                for (int j = 0; j < count; j++)
                {
                    window[j] = values[lo + j];
                }
                Array.Sort(window, 0, count);
                // even truncated windows (at the run's ends) average the two middle values
                result[i] = count % 2 == 1
                    ? window[count / 2]
                    : (window[count / 2 - 1] + window[count / 2]) / 2f;
            }
            return result;
        }

        /// <summary>
        /// Centered Savitzky-Golay smoothing: least-squares quadratic/cubic fit over the
        /// window, evaluated at the center - follows real curvature (braking spikes, corner
        /// peaks) that a plain average flattens. For a symmetric window the fit reduces to
        /// fixed convolution weights with a closed form (degree 2 and 3 give the same center
        /// value), so no matrix solve is needed:
        ///   c(i) = (3m^2 - 7 - 20i^2) / 4  /  (m(m^2 - 4) / 3),  i in [-half, half], m = 2*half+1
        /// e.g. m=5 gives the textbook (-3, 12, 17, 12, -3)/35. Near the run's ends the
        /// window shrinks symmetrically to what fits; below m=5 the fit reproduces the raw
        /// sample exactly (a quadratic through 3 points is exact), so a window of 5+ is
        /// needed to smooth anything. O(n * w).
        /// </summary>
        static float[] SavitzkyGolay(IList<float> values, int half)
        {
            int n = values.Count;
            float[] result = new float[n];
            for (int i = 0; i < n; i++)
            {
                int effectiveHalf = Math.Min(half, Math.Min(i, n - 1 - i));
                if (effectiveHalf < 2)
                {
                    result[i] = values[i]; // m < 5: the fit is exact, no smoothing possible
                    continue;
                }
                int m = 2 * effectiveHalf + 1;
                double norm = m * ((double)m * m - 4) / 3.0;
                double sum = 0;
                for (int j = -effectiveHalf; j <= effectiveHalf; j++)
                {
                    double weight = (3.0 * m * m - 7 - 20.0 * j * j) / 4.0;
                    sum += weight * values[i + j];
                }
                result[i] = (float)(sum / norm);
            }
            return result;
        }
    }
}
