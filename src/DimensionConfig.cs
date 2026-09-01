using System;

namespace DimensionSync
{
    /// <summary>
    /// The half of the field registry that reads KSP config nodes. Kept apart from
    /// DimensionDescriptor.cs so that file, and the propagation rules that go with
    /// it, can be compiled into the unit tests without KSP present.
    /// </summary>
    internal static partial class DimensionFieldRegistry
    {
        /// <summary>
        /// Merge FIELD nodes from a DIMENSION_SYNC config node. Later entries
        /// override earlier ones with the same module/field pair, and an entry that
        /// names an existing pair inherits whatever it does not restate.
        /// </summary>
        public static void LoadConfig(ConfigNode node)
        {
            if (node == null) return;
            foreach (ConfigNode fieldNode in node.GetNodes("FIELD"))
            {
                // Every key is optional. An entry that names an existing module and
                // field inherits that descriptor and restates only what it changes,
                // so "turn this one off" is a two-line patch.
                string field = fieldNode.GetValue("field");
                if (string.IsNullOrEmpty(field)) continue;

                string module = fieldNode.GetValue("module");
                if (string.IsNullOrEmpty(module)) module = null;

                var d = new DimensionDescriptor { ModuleName = module, FieldName = field };

                DimensionDescriptor existing = module == null
                    ? (Wildcard.TryGetValue(field, out var w) ? w : null)
                    : (Qualified.TryGetValue(module + "." + field, out var q) ? q : null);
                if (existing != null)
                {
                    d.IsRadius = existing.IsRadius;
                    d.Channel = existing.Channel;
                    d.Link = existing.Link;
                    d.Ends = existing.Ends;
                    d.Mirrored = existing.Mirrored;
                    d.RequiresEditorControl = existing.RequiresEditorControl;
                    d.RequireGuiUnits = existing.RequireGuiUnits;
                    d.RequireFieldName = existing.RequireFieldName;
                    d.RequireFieldValue = existing.RequireFieldValue;
                    d.SpanAxis = existing.SpanAxis;
                    d.Enabled = existing.Enabled;
                }

                d.IsRadius = ParseBool(fieldNode.GetValue("isRadius"), d.IsRadius);
                d.Enabled = ParseBool(fieldNode.GetValue("enabled"), d.Enabled);
                d.Mirrored = ParseBool(fieldNode.GetValue("mirrored"), d.Mirrored);
                d.RequiresEditorControl =
                    ParseBool(fieldNode.GetValue("requiresEditorControl"), d.RequiresEditorControl);

                string channel = fieldNode.GetValue("channel");
                if (!string.IsNullOrEmpty(channel)) d.Channel = channel.Trim();

                string units = fieldNode.GetValue("requireGuiUnits");
                if (units != null) d.RequireGuiUnits = units.Trim().Length == 0 ? null : units.Trim();

                // requireTrue / requireFalse name a sibling boolean KSPField that has
                // to hold that value for this dimension to count at all.
                string requireTrue = fieldNode.GetValue("requireTrue");
                if (requireTrue != null)
                {
                    d.RequireFieldName = requireTrue.Trim().Length == 0 ? null : requireTrue.Trim();
                    d.RequireFieldValue = true;
                }
                string requireFalse = fieldNode.GetValue("requireFalse");
                if (requireFalse != null)
                {
                    d.RequireFieldName = requireFalse.Trim().Length == 0 ? null : requireFalse.Trim();
                    d.RequireFieldValue = false;
                }

                // A comma or space separated list: "stack", "span", "edge", or any
                // combination of them.
                string link = fieldNode.GetValue("link");
                if (!string.IsNullOrEmpty(link))
                {
                    LinkKind kinds = 0;
                    foreach (string word in link.Split(new[] { ',', ' ', '|' },
                                                       StringSplitOptions.RemoveEmptyEntries))
                    {
                        switch (word.Trim().ToLowerInvariant())
                        {
                            case "stack": kinds |= LinkKind.Stack; break;
                            case "span": kinds |= LinkKind.Span; break;
                            case "edge": kinds |= LinkKind.Edge; break;
                            case "none": break;   // watched, but never crosses a joint
                        }
                    }
                    if (kinds != 0) d.Link = kinds;
                }

                string spanAxis = fieldNode.GetValue("spanAxis");
                if (!string.IsNullOrEmpty(spanAxis))
                {
                    switch (spanAxis.Trim().ToLowerInvariant())
                    {
                        case "x": d.SpanAxis = Axis.X; break;
                        case "y": d.SpanAxis = Axis.Y; break;
                        case "z": d.SpanAxis = Axis.Z; break;
                    }
                }

                string ends = fieldNode.GetValue("ends");
                if (!string.IsNullOrEmpty(ends))
                {
                    switch (ends.Trim().ToLowerInvariant())
                    {
                        case "top":
                        case "tip":
                            d.Ends = PartEnd.Top;
                            break;
                        case "bottom":
                        case "root":
                            d.Ends = PartEnd.Bottom;
                            break;
                        case "none":
                            d.Ends = PartEnd.None;
                            break;
                        default:
                            d.Ends = PartEnd.Both;
                            break;
                    }
                }

                Register(d);
            }
        }

        /// <summary>
        /// Parse a config boolean, keeping <paramref name="fallback"/> when the key
        /// is absent or unparseable, so a typo in a patch cannot silently flip a
        /// setting to false.
        /// </summary>
        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            return bool.TryParse(value.Trim(), out bool b) ? b : fallback;
        }
    }
}
