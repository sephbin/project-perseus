using System.ComponentModel;
using System.Windows.Media;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>One row of the federator review grid (ui/FederatorWindow): a change plus its folded review state.</summary>
    internal class ChangeRow : INotifyPropertyChanged
    {
        internal static readonly Brush AddedBrush = Frozen(0x2E, 0xAD, 0x4B);
        internal static readonly Brush DeletedBrush = Frozen(0xD2, 0x3A, 0x3A);
        internal static readonly Brush ModifiedBrush = Frozen(0xE0, 0x82, 0x00);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush QueryBrush = Frozen(0xE6, 0x00, 0x7E);
        private static readonly Brush AcceptedBrush = Frozen(0x2E, 0xAD, 0x4B);
        private static readonly Brush ResolvedBrush = Frozen(0x00, 0xA0, 0xE0);

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised when the user ticks / unticks a row's Preview box (not for SetPreviewQuietly).</summary>
        internal static event System.Action<ChangeRow> PreviewChanged;

        private bool _preview = true;

        /// <summary>Drawn in the view (and baked with the "shown" scope) when true.</summary>
        public bool Preview
        {
            get => _preview;
            set
            {
                if (_preview == value) return;
                _preview = value;
                Raise(nameof(Preview));
                PreviewChanged?.Invoke(this);
            }
        }

        /// <summary>Only noise under the link's NoiseRules (hidden by "Hide noise").</summary>
        internal bool IsNoise { get; private set; }

        internal void SetPreviewQuietly(bool value)
        {
            if (_preview == value) return;
            _preview = value;
            Raise(nameof(Preview));
        }

        internal ChangeRow(ElementChange change, string linkInstanceUid)
        {
            Change = change;
            Key = ReviewLog.Key(linkInstanceUid, change.UniqueId);
            Element = ChangeDescriber.TypeName(change.Family, change.Type);
            Shown = change;
            Summary = ChangeDescriber.Summary(change);
            State = new ElementReviewState { Key = Key };
        }

        internal ElementChange Change { get; }
        internal string Key { get; }
        internal ElementReviewState State { get; private set; }

        public string GroupKey { get; set; }

        public string KindGlyph => Change.Kind == ChangeKind.Added ? "+" : Change.Kind == ChangeKind.Deleted ? "−" : "~";
        public Brush KindBrush => KindBrushOf(Change.Kind);
        public int KindOrder => (int)Change.Kind;
        public string KindName => Change.Kind.ToString();
        public string Element { get; }
        public string Category => Change.Category ?? "";
        public string Level => Change.Level ?? "";
        public long Id => Change.Id;
        public string Summary { get; private set; }

        /// <summary>The change with noise removed (NoiseFilter.Effective); Change itself when there's no noise.</summary>
        internal ElementChange Shown { get; private set; }

        internal void SetShown(ElementChange effective)
        {
            IsNoise = effective == null;
            Shown = effective ?? Change;
            Summary = IsNoise ? "Noise only" : ChangeDescriber.Summary(Shown);
            Raise(nameof(Summary));
        }

        public string Status => State.Status + (State.StatusCarried ? " ↺" : "");
        public Brush StatusBrush => StatusBrushOf(State.Status);
        public string Assignee => State.Assignees;
        public string Comments => State.CommentCount > 0 ? State.CommentCount.ToString() : "";

        internal void SetState(ElementReviewState state)
        {
            State = state ?? new ElementReviewState { Key = Key };
            Raise(nameof(Status));
            Raise(nameof(StatusBrush));
            Raise(nameof(Assignee));
            Raise(nameof(Comments));
        }

        internal static Brush KindBrushOf(ChangeKind kind) =>
            kind == ChangeKind.Added ? AddedBrush : kind == ChangeKind.Deleted ? DeletedBrush : ModifiedBrush;

        internal static Brush StatusBrushOf(string status)
        {
            switch (status)
            {
                case ReviewStatus.Query: return QueryBrush;
                case ReviewStatus.Accepted: return AcceptedBrush;
                case ReviewStatus.Resolved: return ResolvedBrush;
                default: return Muted;
            }
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
