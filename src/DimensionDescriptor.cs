using System;
using System.Collections.Generic;

namespace DimensionSync
{
    /// <summary>
    /// Which end(s) of a part a dimension field describes.
    /// </summary>
    /// <remarks>
    /// For a stack part these are the part's own -Y and +Y ends. For a wing they
    /// are the root (the end attached to the parent) and the tip; config accepts
    /// "root" and "tip" as spellings of Bottom and Top.
    /// </remarks>
    [Flags]
    internal enum PartEnd
    {
        /// <summary>Neither end; a field marked this way is never propagated.</summary>
        None = 0,

        /// <summary>The part's -Y end, or a wing's root.</summary>
        Bottom = 1,

        /// <summary>The part's +Y end, or a wing's tip.</summary>
        Top = 2,

        /// <summary>A dimension shared by both ends, such as a cylinder's diameter.</summary>
        Both = Bottom | Top,
    }

    /// <summary>
    /// How two parts have to be joined for a dimension to travel between them.
    /// </summary>
    /// <remarks>
    /// A dimension may name more than one kind. A wing's thickness, for instance,
    /// travels both root-to-tip along a chain of wing segments and sideways onto a
    /// control surface lying against that same wing.
    /// </remarks>
    [Flags]
    internal enum LinkKind
    {
        /// <summary>
        /// Crosses no joint at all. The field is still watched, so a change to it is
        /// noticed and anything that reacts to the shape of a joint can act on it,
        /// but the value itself is never copied onto a neighbour.
        /// </summary>
        None = 0,

        /// <summary>Joined by a pair of axial stack attach nodes: tanks, adapters, fairings.</summary>
        Stack = 1,

        /// <summary>
        /// Joined end to end: one part surface-attached at another's tip, which is
        /// how procedural wing segments are chained. The child's root meets the
        /// parent's tip.
        /// </summary>
        Span = 2,

        /// <summary>
        /// Joined side by side: one part surface-attached along another's flank, the
        /// way a control surface sits on a wing's trailing edge. The two run
        /// alongside each other, so root meets root and tip meets tip.
        /// </summary>
        Edge = 4,
    }

    /// <summary>A part-local axis, named without depending on UnityEngine.</summary>
    internal enum Axis
    {
        /// <summary>
        /// Not declared: work the axis out by measuring the part.
        /// </summary>
        /// <remarks>
        /// Only for parts nobody has told us about. Measuring means reading mesh
        /// bounds, and a mesh a procedural mod is part way through rebuilding can
        /// report anything at all - which is a much worse failure than a wrong
        /// guess, because it is intermittent.
        /// </remarks>
        Auto = -1,

        X = 0,
        Y = 1,
        Z = 2,
    }

    /// <summary>
    /// Describes the meaning of one KSPField that holds a part dimension.
    /// </summary>
    internal class DimensionDescriptor
    {
        /// <summary>PartModule class name this applies to, or null for "any module".</summary>
        public string ModuleName;

        /// <summary>Name of the KSPField / BaseField.</summary>
        public string FieldName;

        /// <summary>True when the field stores a radius; the diameter is then value * 2.</summary>
        public bool IsRadius;

        /// <summary>
        /// Which quantity this is. Dimensions only ever synchronise with dimensions
        /// on the same channel, so a wing's chord never leaks into its thickness and
        /// a hollow part's bore never leaks into its outside diameter.
        /// </summary>
        public string Channel = Channels.Outer;

        /// <summary>Which kinds of joint the dimension travels over.</summary>
        public LinkKind Link = LinkKind.Stack;

        /// <summary>
        /// The part-local axis running from the part's root to its tip. Used to tell
        /// an end-to-end joint from a side-by-side one: a part surface-attached by a
        /// node facing along this axis is meeting its neighbour end to end, while one
        /// whose node faces across it is lying against its neighbour's flank.
        /// </summary>
        /// <remarks>
        /// Defaults to Auto, which measures the part. Declare it wherever the answer
        /// is known - a measurement can be wrong, and being wrong intermittently is
        /// far harder to live with than being wrong consistently.
        /// </remarks>
        public Axis SpanAxis = Axis.Auto;

        /// <summary>Which end(s) of the part this field controls.</summary>
        public PartEnd Ends = PartEnd.Both;

        /// <summary>
        /// True when the two sides of a joint hold equal and opposite values rather
        /// than equal ones - a wing's root offset against its neighbour's tip offset.
        /// Mirrored channels never carry through a part to its far end.
        /// </summary>
        public bool Mirrored;

        /// <summary>
        /// False for fields a mod drives from its own window instead of the part
        /// action window, which are hidden from the PAW and have no UI control.
        /// </summary>
        public bool RequiresEditorControl = true;

        /// <summary>
        /// When set, only treat the field as this dimension while its guiUnits say
        /// so. TweakScale's one slider is metres for stack scale types and a
        /// percentage for the rest, and only the former is a diameter.
        /// </summary>
        public string RequireGuiUnits;

        /// <summary>
        /// Name of a sibling boolean KSPField that gates this dimension, or null.
        /// The dimension is only ever read or written while that field holds
        /// <see cref="RequireFieldValue"/>.
        /// </summary>
        public string RequireFieldName = null;

        /// <summary>The value <see cref="RequireFieldName"/> has to hold.</summary>
        public bool RequireFieldValue = false;

        /// <summary>Set false (via config) to make DimensionSync ignore a field entirely.</summary>
        public bool Enabled = true;

        /// <summary>
        /// Convert the field's own value into the units the propagator compares in:
        /// diameters for a stack dimension, and the field's own units otherwise.
        /// </summary>
        public float ToCommonUnits(float rawValue) => IsRadius ? rawValue * 2f : rawValue;

        /// <summary>The inverse of <see cref="ToCommonUnits"/>.</summary>
        public float ToFieldUnits(float value) => IsRadius ? value * 0.5f : value;

        /// <summary>Registry key: "Module.field" for a specific module, bare "field" for the wildcard.</summary>
        public string Key => ModuleName == null ? FieldName : ModuleName + "." + FieldName;

        /// <summary>Short form used in the debug log.</summary>
        public override string ToString() => Key + " [" + Channel + "]";
    }

    /// <summary>Channel names with built-in meaning. Any other string works too.</summary>
    internal static class Channels
    {
        /// <summary>How far a part reaches from root to tip.</summary>
        public const string Span = "span";

        /// <summary>The outside of a stack part. The default.</summary>
        public const string Outer = "outer";

        /// <summary>The bore of a hollow stack part.</summary>
        public const string Inner = "inner";

        /// <summary>A wing's chord.</summary>
        public const string Width = "width";

        /// <summary>A wing's thickness.</summary>
        public const string Thickness = "thickness";

        /// <summary>The width of a wing's leading edge.</summary>
        public const string EdgeLeading = "edgeLeading";

        /// <summary>The width of a wing's trailing edge.</summary>
        public const string EdgeTrailing = "edgeTrailing";

        /// <summary>A wing's sweep offset. Mirrored, and off by default.</summary>
        public const string Offset = "offset";
    }

    /// <summary>
    /// The catalogue of known dimension fields.
    /// </summary>
    /// <remarks>
    /// Entries are matched most-specific-first: an exact
    /// <c>ModuleClassName.fieldName</c> entry wins over a bare <c>fieldName</c>
    /// entry, and base classes of the module are consulted before the wildcard.
    /// The built-in table below is a starting point; everything can be added to,
    /// overridden or disabled from <c>DimensionSync.cfg</c> (and therefore from
    /// ModuleManager patches shipped by other mods).
    /// </remarks>
    internal static partial class DimensionFieldRegistry
    {
        private static readonly Dictionary<string, DimensionDescriptor> Qualified =
            new Dictionary<string, DimensionDescriptor>(StringComparer.Ordinal);

        private static readonly Dictionary<string, DimensionDescriptor> Wildcard =
            new Dictionary<string, DimensionDescriptor>(StringComparer.Ordinal);

        /// <summary>All field names we know about, used as a cheap pre-filter.</summary>
        private static readonly HashSet<string> KnownFieldNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Populate the built-in catalogue the first time anything touches the registry.</summary>
        static DimensionFieldRegistry()
        {
            LoadDefaults();
        }

        /// <summary>
        /// The shipped catalogue. Config is merged on top of this, so anything here
        /// can be retuned or switched off without touching code.
        /// </summary>
        private static void LoadDefaults()
        {
            // -----------------------------------------------------------------
            // Stack diameters
            // -----------------------------------------------------------------
            // Generic names, shared by several mods with the same meaning. The
            // "current*" spellings come from SSTU and everything descended from it,
            // which includes ROLib and its forks.
            Add(null, "diameter");
            Add(null, "outerDiameter");
            Add(null, "innerDiameter", channel: Channels.Inner);
            Add(null, "topDiameter", ends: PartEnd.Top);
            Add(null, "bottomDiameter", ends: PartEnd.Bottom);
            Add(null, "topOuterDiameter", ends: PartEnd.Top);
            Add(null, "bottomOuterDiameter", ends: PartEnd.Bottom);
            Add(null, "topInnerDiameter", ends: PartEnd.Top, channel: Channels.Inner);
            Add(null, "bottomInnerDiameter", ends: PartEnd.Bottom, channel: Channels.Inner);
            Add(null, "currentDiameter");
            Add(null, "currentTopDiameter", ends: PartEnd.Top);
            Add(null, "currentBottomDiameter", ends: PartEnd.Bottom);
            Add(null, "radius", isRadius: true);
            Add(null, "topRadius", isRadius: true, ends: PartEnd.Top);
            Add(null, "bottomRadius", isRadius: true, ends: PartEnd.Bottom);

            // ProceduralParts. Its shape modules all live on the part at once and
            // only the selected one is enabled, which the adjustability check
            // takes care of.
            Add("ProceduralShapeCylinder", "diameter");
            Add("ProceduralShapePill", "diameter");
            Add("ProceduralShapePolygon", "diameter");
            Add("ProceduralShapeCone", "topDiameter", ends: PartEnd.Top);
            Add("ProceduralShapeCone", "bottomDiameter", ends: PartEnd.Bottom);
            Add("ProceduralShapeBezierCone", "topDiameter", ends: PartEnd.Top);
            Add("ProceduralShapeBezierCone", "bottomDiameter", ends: PartEnd.Bottom);
            Add("ProceduralShapeHollowCylinder", "outerDiameter");
            Add("ProceduralShapeHollowCylinder", "innerDiameter", channel: Channels.Inner);
            Add("ProceduralShapeHollowPill", "outerDiameter");
            Add("ProceduralShapeHollowPill", "innerDiameter", channel: Channels.Inner);
            Add("ProceduralShapeHollowCone", "topOuterDiameter", ends: PartEnd.Top);
            Add("ProceduralShapeHollowCone", "bottomOuterDiameter", ends: PartEnd.Bottom);
            Add("ProceduralShapeHollowCone", "topInnerDiameter", ends: PartEnd.Top, channel: Channels.Inner);
            Add("ProceduralShapeHollowCone", "bottomInnerDiameter", ends: PartEnd.Bottom, channel: Channels.Inner);
            Add("ProceduralShapeHollowTruss", "topDiameter", ends: PartEnd.Top);
            Add("ProceduralShapeHollowTruss", "bottomDiameter", ends: PartEnd.Bottom);
            // rodDiameter is the truss members, not the part envelope.
            Add("ProceduralShapeHollowTruss", "rodDiameter", enabled: false);

            // Procedural Fairings. baseSize is the diameter of the fairing base's
            // lower (stack) end; topSize belongs to the adapter's upper end.
            Add("ProceduralFairingBase", "baseSize", ends: PartEnd.Bottom);
            Add("ProceduralFairingBase", "topSize", ends: PartEnd.Top);
            // The interstage node ring radius, off by default: it is where payload
            // nodes sit, not the outside of the part.
            Add("KzNodeNumberTweaker", "radius", isRadius: true, enabled: false);

            // TweakScale's single slider is a diameter in metres only for stack
            // scale types; for the rest it is a percentage or an index, hence the
            // guiUnits guard. Off by default because scaling a part changes its
            // length and mass too, which is a bigger change than the player asked
            // for when they dragged a neighbour's diameter.
            Add("TweakScale", "tweakScale", requireGuiUnits: "m", enabled: false);

            // Parachute canopy diameters are not part diameters.
            Add("RealChuteModule", "deployedDiameter", enabled: false);
            Add("RealChuteModule", "preDeployedDiameter", enabled: false);
            Add("Parachute", "deployedDiameter", enabled: false);
            Add("Parachute", "preDeployedDiameter", enabled: false);

            // -----------------------------------------------------------------
            // Wing spans
            // -----------------------------------------------------------------
            // B9 Procedural Wings and its forks, which all keep these field names
            // because they are persisted into craft files. The fields are driven
            // from the mod's own window rather than the PAW, so they are hidden and
            // have no UI control - but WingProcedural re-reads them every Update
            // and rebuilds itself, so writing the field is enough.
            // Chord and edge widths are end-to-end only. A control surface's chord is
            // how far forward the flap reaches, which has nothing to do with the
            // chord of the wing it sits on, so it must not cross a side-by-side
            // joint. B9 takes the same view: its own InheritBase opens with
            // "if (!parent.isCtrlSrf && !isCtrlSrf)".
            AddWing("WingProcedural", "sharedBaseWidthRoot", Channels.Width, PartEnd.Bottom);
            AddWing("WingProcedural", "sharedBaseWidthTip", Channels.Width, PartEnd.Top);
            AddWing("WingProcedural", "sharedEdgeWidthLeadingRoot", Channels.EdgeLeading, PartEnd.Bottom);
            AddWing("WingProcedural", "sharedEdgeWidthLeadingTip", Channels.EdgeLeading, PartEnd.Top);
            AddWing("WingProcedural", "sharedEdgeWidthTrailingRoot", Channels.EdgeTrailing, PartEnd.Bottom);
            AddWing("WingProcedural", "sharedEdgeWidthTrailingTip", Channels.EdgeTrailing, PartEnd.Top);

            // Thickness travels both ways: on to the next segment out, and sideways
            // on to a control surface lying against this one, which has to be as
            // thick as the wing where the two meet.
            AddWing("WingProcedural", "sharedBaseThicknessRoot", Channels.Thickness, PartEnd.Bottom,
                    link: LinkKind.Span | LinkKind.Edge);
            AddWing("WingProcedural", "sharedBaseThicknessTip", Channels.Thickness, PartEnd.Top,
                    link: LinkKind.Span | LinkKind.Edge);

            // Span is side-by-side only: consecutive wing segments have lengths of
            // their own, but a control surface running along a wing should be as long
            // as the wing.
            AddWing("WingProcedural", "sharedBaseLength", Channels.Span, PartEnd.Both,
                    link: LinkKind.Edge);
            // Sweep offsets are watched but never copied across a joint. B9's own
            // "inherit base" button answers a swept tip by shearing the next segment
            // out to match (sharedBaseOffsetRoot = -parent.sharedBaseOffsetTip),
            // which keeps the joint closed by changing the neighbour's SHAPE. That is
            // not what a player sweeping a wing wants: the segment outboard should
            // keep the planform it was given and simply move along with the tip it is
            // attached to. WingConformance does that instead, and a control surface's
            // offsets - which are a sweep RATE rather than a distance - are handled
            // there too.
            AddWing("WingProcedural", "sharedBaseOffsetRoot", Channels.Offset, PartEnd.Bottom,
                    link: LinkKind.None, mirrored: true);
            AddWing("WingProcedural", "sharedBaseOffsetTip", Channels.Offset, PartEnd.Top,
                    link: LinkKind.None, mirrored: true);
        }

        /// <summary>Register a stack dimension - one that travels over axial attach nodes.</summary>
        /// <param name="module">PartModule class name, or null to match the field on any module.</param>
        private static void Add(string module, string field, bool isRadius = false,
                                string channel = Channels.Outer,
                                PartEnd ends = PartEnd.Both, bool enabled = true,
                                string requireGuiUnits = null)
        {
            Register(new DimensionDescriptor
            {
                ModuleName = module,
                FieldName = field,
                IsRadius = isRadius,
                Channel = channel,
                Ends = ends,
                Enabled = enabled,
                RequireGuiUnits = requireGuiUnits,
            });
        }

        /// <summary>
        /// Register a wing dimension - one that travels root-to-tip over surface
        /// attachment, and that its mod drives from its own window rather than the
        /// part action window.
        /// </summary>
        private static void AddWing(string module, string field, string channel, PartEnd ends,
                                    LinkKind link = LinkKind.Span,
                                    bool mirrored = false, bool enabled = true)
        {
            Register(new DimensionDescriptor
            {
                ModuleName = module,
                FieldName = field,
                Channel = channel,
                Link = link,
                Ends = ends,
                Mirrored = mirrored,
                // Every B9 wing and control surface runs root to tip along its own
                // +X, so say so rather than making the mod measure for it.
                SpanAxis = Axis.X,
                RequiresEditorControl = false,
                Enabled = enabled,
            });
        }

        /// <summary>
        /// File a descriptor under its module-qualified name or, when it names no
        /// module, under the wildcard table. Re-registering a key replaces it, which
        /// is how config overrides the defaults.
        /// </summary>
        private static void Register(DimensionDescriptor d)
        {
            if (string.IsNullOrEmpty(d.FieldName)) return;
            if (d.ModuleName == null) Wildcard[d.FieldName] = d;
            else Qualified[d.Key] = d;
            KnownFieldNames.Add(d.FieldName);
        }

        /// <summary>Cheap test used before walking a module's field list.</summary>
        public static bool IsKnownFieldName(string fieldName) => KnownFieldNames.Contains(fieldName);

        /// <summary>
        /// Resolve the descriptor for <paramref name="fieldName"/> on a module of
        /// type <paramref name="moduleType"/>, or null when the field is unknown or
        /// has been disabled.
        /// </summary>
        public static DimensionDescriptor Lookup(Type moduleType, string fieldName)
        {
            // Walk up the module's inheritance chain before falling back to the
            // wildcard, so an entry written for a base class covers every mod that
            // derives from it - which is how one SSTU entry covers its forks.
            for (Type t = moduleType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (Qualified.TryGetValue(t.Name + "." + fieldName, out DimensionDescriptor d))
                    return d.Enabled ? d : null;
            }
            if (Wildcard.TryGetValue(fieldName, out DimensionDescriptor w))
                return w.Enabled ? w : null;
            return null;
        }
    }
}
