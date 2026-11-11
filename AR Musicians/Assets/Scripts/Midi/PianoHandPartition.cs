namespace ARMusician.Midi
{
    public enum HandSelection { Both, Left, Right }
    public enum PianoHand { Unspecified, Left, Right }

    public static class PianoHandPartition
    {
        public const int DefaultSplitPitch = 60; // C4 is the first right-hand pitch.

        public static PianoHand Assign(int pitch, PianoHand sourceHand, int splitPitch)
        {
            if (pitch < 0 || pitch > 127) return PianoHand.Unspecified;
            if (sourceHand == PianoHand.Left || sourceHand == PianoHand.Right) return sourceHand;
            return pitch < splitPitch ? PianoHand.Left : PianoHand.Right;
        }

        public static bool Includes(HandSelection selection, PianoHand hand)
        {
            return selection == HandSelection.Both ||
                (selection == HandSelection.Left && hand == PianoHand.Left) ||
                (selection == HandSelection.Right && hand == PianoHand.Right);
        }

        internal static PianoHand ParseTrackName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return PianoHand.Unspecified;
            string normalized = name.ToLowerInvariant().Replace(" ", "").Replace("\t", "")
                .Replace("-", "").Replace("_", "").Replace(".", "").Replace("(", "").Replace(")", "");
            if (normalized.StartsWith("piano", System.StringComparison.Ordinal)) normalized = normalized.Substring(5);
            else if (normalized.EndsWith("piano", System.StringComparison.Ordinal)) normalized = normalized.Substring(0, normalized.Length - 5);
            switch (normalized)
            {
                case "lefthand":
                case "lh": return PianoHand.Left;
                case "righthand":
                case "rh": return PianoHand.Right;
                default: return PianoHand.Unspecified;
            }
        }
    }
}
