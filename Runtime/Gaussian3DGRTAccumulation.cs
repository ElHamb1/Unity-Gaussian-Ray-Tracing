namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Progress of the 3DGRT stochastic temporal accumulation, for tools that
    /// have to wait until the image has actually converged - an offline frame
    /// capture, which would otherwise record partially accumulated noise.
    /// </summary>
    /// <remarks>
    /// A separate public type rather than members on
    /// <c>GaussianSplat3DGRTRenderSystem</c>, because that class is internal to
    /// this assembly and widening it to publish four read-only values would
    /// expose the whole render system with it.
    ///
    /// Static because the caller sits outside the render loop and has no handle
    /// on the custom pass that owns the system. One camera renders 3DGRT in the
    /// usual setup; with several, this describes whichever rendered last, which
    /// is why <see cref="RenderFrame"/> is published alongside.
    /// </remarks>
    public static class Gaussian3DGRTAccumulation
    {
        /// <summary>
        /// True while the trace strategy actually accumulates over frames.
        /// Deterministic tracing has nothing to accumulate.
        /// </summary>
        public static bool SamplingActive { get; private set; }

        /// <summary>Samples accumulated so far, counting up to <see cref="SampleCount"/>.</summary>
        public static int SampleId { get; private set; }

        /// <summary>Samples this image will be accumulated from.</summary>
        public static int SampleCount { get; private set; }

        /// <summary>Time.frameCount when the values above were last written, or -1.</summary>
        public static int RenderFrame { get; private set; } = -1;

        /// <summary>
        /// Increments every time the accumulation is thrown away and started
        /// over - because the scene changed, the camera moved, or a tracked
        /// setting was edited.
        /// </summary>
        /// <remarks>
        /// This is what lets a caller tell "converged on the content I asked
        /// for" from "still converged on the previous content". Watching
        /// <see cref="Complete"/> alone cannot: at a low sample count the
        /// restart and the convergence can both land between two polls, so the
        /// observer sees an unbroken run of Complete and waits for a transition
        /// that already happened. Comparing this counter against one sampled
        /// before the change has no such window.
        /// </remarks>
        public static int RestartCount { get; private set; }

        /// <summary>
        /// True once the accumulated image is final. Always true for
        /// deterministic tracing, which finishes every frame it draws.
        /// </summary>
        public static bool Complete =>
            !SamplingActive || (SampleCount > 0 && SampleId >= SampleCount);

        /// <summary>Fraction of the accumulation that is done, for progress reporting.</summary>
        public static float Progress =>
            !SamplingActive ? 1f :
            SampleCount > 0 ? UnityEngine.Mathf.Clamp01(SampleId / (float)SampleCount) : 0f;

        /// <summary>
        /// Overrides the custom pass's Temporal Sample Count while it is
        /// greater than zero. 0 restores the pass's own value.
        /// </summary>
        /// <remarks>
        /// For tools that need to trade convergence against time without the
        /// user hunting down the pass - an offline capture running a fast draft
        /// and then a final pass, say. A static seam because the pass type is
        /// internal to this assembly, and because it need not even be reachable:
        /// a CustomPass lives inside a CustomPassVolume rather than being a
        /// component anything else can hold a reference to.
        ///
        /// The sample count is part of the stochastic state hash, so changing
        /// this restarts the accumulation rather than reusing history that was
        /// gathered under the old count.
        ///
        /// Whoever sets it owns restoring it; leaving it set silently changes
        /// every later render.
        /// </remarks>
        public static int SampleCountOverride { get; set; }

        /// <summary>Applies <see cref="SampleCountOverride"/> to an authored count.</summary>
        public static int ResolveSampleCount(int authoredSampleCount)
        {
            int over = SampleCountOverride;
            return over > 0 ? over : authoredSampleCount;
        }

        /// <summary>
        /// Clears the override and the readout at the start of every play
        /// session.
        /// </summary>
        /// <remarks>
        /// Statics survive entering Play Mode when domain reload is disabled,
        /// so without this a capture that was interrupted - or that threw -
        /// would silently keep forcing its draft sample count on every later
        /// session. SubsystemRegistration runs before the first scene loads,
        /// which is early enough that nothing has rendered yet.
        /// </remarks>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay()
        {
            SampleCountOverride = 0;
            SamplingActive = false;
            SampleId = 0;
            SampleCount = 0;
            RenderFrame = -1;
            RestartCount = 0;
            LastSplatCount = 0;
            LastTransmissionMeshCount = 0;
            LastPassRan = false;
        }

        /// <summary>
        /// What the 3DGRT pass found on its last attempt, so "nothing is being
        /// drawn" can be attributed instead of guessed at.
        /// </summary>
        /// <remarks>
        /// Ray-only surfaces have no rasterized fallback, so when the pass
        /// declines to run they vanish with no error anywhere. These three say
        /// whether it ran and what it had to trace.
        /// </remarks>
        public static int LastSplatCount { get; private set; }

        /// <summary>Transmission meshes gathered for the last camera.</summary>
        public static int LastTransmissionMeshCount { get; private set; }

        /// <summary>Whether the pass decided to trace at all.</summary>
        public static bool LastPassRan { get; private set; }

        internal static void ReportPass(int splatCount, int meshCount, bool ran)
        {
            LastSplatCount = splatCount;
            LastTransmissionMeshCount = meshCount;
            LastPassRan = ran;
        }

        internal static void ReportRestart()
        {
            unchecked
            {
                ++RestartCount;
            }
        }

        internal static void ReportTemporal(int sampleId, int sampleCount, int renderFrame)
        {
            SamplingActive = true;
            SampleId = sampleId;
            SampleCount = sampleCount;
            RenderFrame = renderFrame;
        }

        internal static void ReportDeterministic(int renderFrame)
        {
            SamplingActive = false;
            SampleId = 0;
            SampleCount = 0;
            RenderFrame = renderFrame;
        }
    }
}
