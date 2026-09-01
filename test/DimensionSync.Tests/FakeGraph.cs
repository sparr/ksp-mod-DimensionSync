using System.Collections.Generic;

namespace DimensionSync.Tests
{
    /// <summary>An in-memory dimension slot, standing in for a KSPField on a part.</summary>
    internal class FakeSlot : IDimensionSlot
    {
        /// <summary>What this field means; fixed for the life of the slot.</summary>
        private readonly DimensionDescriptor _descriptor;

        /// <summary>Value in the field's own units (halved for radius fields).</summary>
        public float Raw;

        /// <summary>Clear to model a field the player could not have changed.</summary>
        public bool Adjustable = true;

        /// <summary>Optional clamp applied on write, mimicking a UI_FloatEdit range.</summary>
        /// <summary>Lower end of the optional clamp applied on write.</summary>
        public float Min = float.NegativeInfinity;

        /// <summary>Upper end of the optional clamp applied on write.</summary>
        public float Max = float.PositiveInfinity;

        /// <summary>How many times this slot has been written, so a test can assert it was left alone.</summary>
        public int Writes;

        /// <summary>Create a slot holding <paramref name="value"/> in common units.</summary>
        public FakeSlot(DimensionDescriptor descriptor, float value)
        {
            _descriptor = descriptor;
            Raw = descriptor.ToFieldUnits(value);
        }

        /// <summary>What this slot means to the propagator.</summary>
        public DimensionDescriptor Descriptor => _descriptor;

        /// <summary>Backed by <see cref="Adjustable"/> so a test can make a slot read-only.</summary>
        public bool IsAdjustable => Adjustable;

        /// <summary>The value in common units, doubled for a radius field.</summary>
        public float Value => _descriptor.ToCommonUnits(Raw);

        /// <summary>"part.field", for readable assertion messages.</summary>
        public string Label => Owner?.Label + "." + _descriptor.FieldName;

        /// <summary>The part this slot belongs to.</summary>
        public FakeNode Owner;

        /// <summary>
        /// Record the write, clamp it to <see cref="Min"/>..<see cref="Max"/> the way
        /// a UI_FloatEdit range would, and report back what stuck.
        /// </summary>
        public float SetValue(float value)
        {
            Writes++;
            float clamped = value < Min ? Min : value > Max ? Max : value;
            Raw = _descriptor.ToFieldUnits(clamped);
            return Value;
        }
    }

    /// <summary>An in-memory part.</summary>
    internal class FakeNode : IDimensionNode
    {
        /// <summary>Identifies the part in assertion messages.</summary>
        public string Name;

        /// <summary>The dimensions on this part.</summary>
        public readonly List<IDimensionSlot> SlotList = new List<IDimensionSlot>();

        /// <summary>The joints leaving this part.</summary>
        public readonly List<AxialLink> LinkList = new List<AxialLink>();

        /// <summary>Create an empty part; give it dimensions with <see cref="Add"/>.</summary>
        public FakeNode(string name) { Name = name; }

        /// <summary>The part's name, for readable assertion messages.</summary>
        public string Label => Name;

        /// <summary>The dimensions on this part.</summary>
        public IList<IDimensionSlot> Slots => SlotList;

        /// <summary>The joints this part takes part in, wired by <see cref="Fake.Stack"/> and <see cref="Fake.Span"/>.</summary>
        public IEnumerable<AxialLink> Links => LinkList;

        /// <summary>Give this part a dimension, starting at <paramref name="value"/> in common units.</summary>
        public FakeSlot Add(DimensionDescriptor descriptor, float value)
        {
            var slot = new FakeSlot(descriptor, value) { Owner = this };
            SlotList.Add(slot);
            return slot;
        }

        /// <summary>Look a dimension up by field name, or null when this part has no such field.</summary>
        public FakeSlot Slot(string fieldName)
        {
            foreach (FakeSlot s in SlotList)
                if (s.Descriptor.FieldName == fieldName) return s;
            return null;
        }
    }

    /// <summary>
    /// Ready-made descriptors and fixtures, so each test reads as the arrangement
    /// it is about rather than as a page of setup.
    /// </summary>
    internal static class Fake
    {
        // --- stack dimensions -------------------------------------------------

        public static readonly DimensionDescriptor Diameter =
            new DimensionDescriptor { FieldName = "diameter", Ends = PartEnd.Both };

        public static readonly DimensionDescriptor TopDiameter =
            new DimensionDescriptor { FieldName = "topDiameter", Ends = PartEnd.Top };

        public static readonly DimensionDescriptor BottomDiameter =
            new DimensionDescriptor { FieldName = "bottomDiameter", Ends = PartEnd.Bottom };

        public static readonly DimensionDescriptor Radius =
            new DimensionDescriptor { FieldName = "radius", Ends = PartEnd.Both, IsRadius = true };

        public static readonly DimensionDescriptor InnerDiameter =
            new DimensionDescriptor { FieldName = "innerDiameter", Ends = PartEnd.Both, Channel = Channels.Inner };

        public static readonly DimensionDescriptor OuterDiameter =
            new DimensionDescriptor { FieldName = "outerDiameter", Ends = PartEnd.Both };

        // --- wing dimensions --------------------------------------------------

        /// <summary>Chord at the end attached to the parent.</summary>
        public static readonly DimensionDescriptor WidthRoot = Wing("widthRoot", Channels.Width, PartEnd.Bottom);

        /// <summary>Chord at the free end.</summary>
        public static readonly DimensionDescriptor WidthTip = Wing("widthTip", Channels.Width, PartEnd.Top);

        /// <summary>Thickness at the root.</summary>
        public static readonly DimensionDescriptor ThicknessRoot =
            Wing("thicknessRoot", Channels.Thickness, PartEnd.Bottom, LinkKind.Span | LinkKind.Edge);

        /// <summary>Thickness at the tip.</summary>
        public static readonly DimensionDescriptor ThicknessTip =
            Wing("thicknessTip", Channels.Thickness, PartEnd.Top, LinkKind.Span | LinkKind.Edge);

        /// <summary>How far the part reaches root to tip. Side-by-side joints only.</summary>
        public static readonly DimensionDescriptor SpanLength =
            Wing("spanLength", Channels.Span, PartEnd.Both, LinkKind.Edge);

        /// <summary>Sweep offset at the root; pairs as the negation of a neighbour's tip offset.</summary>
        public static readonly DimensionDescriptor OffsetRoot =
            Wing("offsetRoot", Channels.Offset, PartEnd.Bottom, mirrored: true);

        /// <summary>Sweep offset at the tip.</summary>
        public static readonly DimensionDescriptor OffsetTip =
            Wing("offsetTip", Channels.Offset, PartEnd.Top, mirrored: true);

        /// <summary>Build a wing-style descriptor: span-linked, no PAW control needed.</summary>
        private static DimensionDescriptor Wing(string field, string channel, PartEnd ends,
                                                LinkKind link = LinkKind.Span, bool mirrored = false) =>
            new DimensionDescriptor
            {
                FieldName = field,
                Channel = channel,
                Link = link,
                Ends = ends,
                Mirrored = mirrored,
                RequiresEditorControl = false,
            };

        // --- fixtures ---------------------------------------------------------

        /// <summary>Join <paramref name="lower"/>'s top to <paramref name="upper"/>'s bottom.</summary>
        public static void Stack(FakeNode lower, FakeNode upper)
        {
            lower.LinkList.Add(new AxialLink(upper, PartEnd.Top, PartEnd.Bottom));
            upper.LinkList.Add(new AxialLink(lower, PartEnd.Bottom, PartEnd.Top));
        }

        /// <summary>Join <paramref name="inboard"/>'s tip to <paramref name="outboard"/>'s root.</summary>
        public static void Span(FakeNode inboard, FakeNode outboard)
        {
            inboard.LinkList.Add(new AxialLink(outboard, PartEnd.Top, PartEnd.Bottom, LinkKind.Span));
            outboard.LinkList.Add(new AxialLink(inboard, PartEnd.Bottom, PartEnd.Top, LinkKind.Span));
        }

        /// <summary>
        /// Lay <paramref name="attached"/> alongside <paramref name="host"/>, the way
        /// a control surface sits on a wing: root against root and tip against tip,
        /// which is two connections rather than one.
        /// </summary>
        public static void Edge(FakeNode host, FakeNode attached)
        {
            host.LinkList.Add(new AxialLink(attached, PartEnd.Bottom, PartEnd.Bottom, LinkKind.Edge));
            host.LinkList.Add(new AxialLink(attached, PartEnd.Top, PartEnd.Top, LinkKind.Edge));
            attached.LinkList.Add(new AxialLink(host, PartEnd.Bottom, PartEnd.Bottom, LinkKind.Edge));
            attached.LinkList.Add(new AxialLink(host, PartEnd.Top, PartEnd.Top, LinkKind.Edge));
        }

        /// <summary>
        /// Lay <paramref name="attached"/> along part of <paramref name="host"/>,
        /// covering the span from <paramref name="from"/> to <paramref name="to"/> as
        /// fractions of the host's root-to-tip length.
        /// </summary>
        /// <remarks>
        /// One-way, as the real thing is: a short flap does not say what either end
        /// of the wing carrying it should be. Values crossing the joint are read off
        /// the line between the host's root and tip values, except span, which is
        /// scaled by how much of the host is covered.
        /// </remarks>
        public static void PartialEdge(FakeNode host, FakeNode attached, float from, float to)
        {
            float fraction = System.Math.Abs(to - from);
            host.LinkList.Add(new AxialLink(attached, PartEnd.Both, PartEnd.Bottom, LinkKind.Edge,
                                            HostStation(host, from, fraction)));
            host.LinkList.Add(new AxialLink(attached, PartEnd.Both, PartEnd.Top, LinkKind.Edge,
                                            HostStation(host, to, fraction)));
        }

        /// <summary>The converter a partial cover applies at one station along the host.</summary>
        private static System.Func<DimensionDescriptor, float, float> HostStation(
            FakeNode host, float station, float fraction)
        {
            return (descriptor, value) =>
            {
                if (descriptor.Channel == Channels.Span) return value * fraction;

                PartEnd wanted = descriptor.Ends == PartEnd.Bottom ? PartEnd.Top : PartEnd.Bottom;
                foreach (FakeSlot slot in host.SlotList)
                {
                    if (slot.Descriptor.Channel != descriptor.Channel) continue;
                    if ((slot.Descriptor.Ends & wanted) == 0) continue;
                    return descriptor.Ends == PartEnd.Bottom
                        ? value + (slot.Value - value) * station
                        : slot.Value + (value - slot.Value) * station;
                }
                return value;
            };
        }

        /// <summary>A wing segment carrying a span length as well as chord and thickness.</summary>
        public static FakeNode SpanningWing(string name, float widthRoot, float widthTip,
                                            float thicknessRoot, float thicknessTip, float span)
        {
            FakeNode node = Wing(name, widthRoot, widthTip, thicknessRoot, thicknessTip);
            node.Add(SpanLength, span);
            return node;
        }

        /// <summary>A plain cylinder with a single "diameter" field covering both ends.</summary>
        public static FakeNode Cylinder(string name, float diameter)
        {
            var node = new FakeNode(name);
            node.Add(Diameter, diameter);
            return node;
        }

        /// <summary>A cone with independent top and bottom diameters.</summary>
        public static FakeNode Cone(string name, float bottom, float top)
        {
            var node = new FakeNode(name);
            node.Add(BottomDiameter, bottom);
            node.Add(TopDiameter, top);
            return node;
        }

        /// <summary>A wing segment with root and tip chord and thickness.</summary>
        public static FakeNode Wing(string name, float widthRoot, float widthTip,
                                    float thicknessRoot = 0.24f, float thicknessTip = 0.24f)
        {
            var node = new FakeNode(name);
            node.Add(WidthRoot, widthRoot);
            node.Add(WidthTip, widthTip);
            node.Add(ThicknessRoot, thicknessRoot);
            node.Add(ThicknessTip, thicknessTip);
            return node;
        }
    }
}
