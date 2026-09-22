using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingGrid = Tekla.Structures.Drawing.Grid;
using DrawingGridLine = Tekla.Structures.Drawing.GridLine;
using DrawingMark = Tekla.Structures.Drawing.Mark;
using DrawingModelObject = Tekla.Structures.Drawing.ModelObject;
using DrawingPart = Tekla.Structures.Drawing.Part;
using DrawingView = Tekla.Structures.Drawing.View;
using DrawingWeldMark = Tekla.Structures.Drawing.WeldMark;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Creates end views without Tekla's CreateSectionView command.
    ///
    /// CreateSectionView was returning a valid view/restriction box but no drawing Part
    /// objects for the detected end plate, even when the plate geometry was demonstrably
    /// inside the restriction volume. This builder therefore owns the end-view geometry:
    /// - detect the physical end plate;
    /// - stand on its outside face and look inward;
    /// - build an explicit end-view coordinate system;
    /// - create a tightly restricted drawing view;
    /// - create the A/B section mark separately in the retained base view.
    ///
    /// The outside face is always attempted first. The inside face is a fallback only.
    /// </summary>
    public sealed class ControlledEndViewBuilder
    {
        private const double ViewGap = 12.0;
        private const double EndViewDepth = 600.0;
        private const double BackDepth = 25.0;
        private const double CrossSectionMargin = 50.0;

        private readonly Model _model;

        public ControlledEndViewBuilder(Model model)
        {
            _model = model;
        }

        public int Build(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var source = GetBaseView(analysis);
            if (source == null || source.MainPartBounds == null)
            {
                messages?.Add("END-CONTROL: no usable base view found.");
                return 0;
            }

            var parts = GetAssemblyParts(analysis.MainPart);
            var allowedIds = new HashSet<int>(parts.Select(part => part.Identifier.ID));
            var detection = DetectEndPlates(analysis.MainPart, parts, messages);
            var created = 0;

            messages?.Add("END-CONTROL =================================================");
            messages?.Add("END-CONTROL strategy: bypass CreateSectionView; build the end view coordinate system and restriction volume directly.");
            messages?.Add("END-CONTROL policy: OUTSIDE face first. INSIDE face only if both controlled outside creation methods fail.");
            messages?.Add("END-CONTROL source DisplayCS: " + Cs(source.View.DisplayCoordinateSystem));

            if (detection.Start != null)
            {
                DeleteGeneratedEnd(analysis.Drawing, "TDA_SECTION_A");
                RemoveExistingSectionMark(source.View, "A");
                if (BuildOneEnd(analysis, source, detection.Start.Part, true, parts, allowedIds, messages) != null)
                    created++;
            }

            if (detection.Finish != null)
            {
                DeleteGeneratedEnd(analysis.Drawing, "TDA_SECTION_B");
                RemoveExistingSectionMark(source.View, "B");
                if (BuildOneEnd(analysis, source, detection.Finish.Part, false, parts, allowedIds, messages) != null)
                    created++;
            }

            messages?.Add("END-CONTROL =================================================");
            analysis.Drawing.CommitChanges();
            return created;
        }

        private DrawingView BuildOneEnd(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            ModelPart target,
            bool startEnd,
            IList<ModelPart> assemblyParts,
            ISet<int> allowedIds,
            IList<string> messages)
        {
            var letter = startEnd ? "A" : "B";
            var targetSourceBox = GetPartBox(target, source.View.DisplayCoordinateSystem);
            var horizontal = Math.Abs(source.MainPartBounds.Width) >= Math.Abs(source.MainPartBounds.Height);

            var memberCentre = horizontal
                ? (source.MainPartBounds.MinX + source.MainPartBounds.MaxX) * 0.5
                : (source.MainPartBounds.MinY + source.MainPartBounds.MaxY) * 0.5;
            var targetCentre = horizontal ? targetSourceBox.Centre.X : targetSourceBox.Centre.Y;
            var targetOnLowSide = targetCentre < memberCentre;

            var outsideCut = horizontal
                ? (targetOnLowSide ? targetSourceBox.Min.X : targetSourceBox.Max.X)
                : (targetOnLowSide ? targetSourceBox.Min.Y : targetSourceBox.Max.Y);
            var insideCut = horizontal
                ? (targetOnLowSide ? targetSourceBox.Max.X : targetSourceBox.Min.X)
                : (targetOnLowSide ? targetSourceBox.Max.Y : targetSourceBox.Min.Y);

            messages?.Add("END-CONTROL " + letter + ": target=" + Describe(target));
            messages?.Add("END-CONTROL " + letter + ": target source box min=" + P(targetSourceBox.Min) +
                          " max=" + P(targetSourceBox.Max) + " centre=" + P(targetSourceBox.Centre));
            messages?.Add("END-CONTROL " + letter + ": member side=" + (targetOnLowSide ? "LOW" : "HIGH") +
                          "; OUTSIDE=" + F(outsideCut) + "; INSIDE=" + F(insideCut) + ".");

            var outside = TryControlledEnd(
                analysis,
                source,
                target,
                startEnd,
                assemblyParts,
                allowedIds,
                horizontal,
                targetOnLowSide,
                outsideCut,
                false,
                "OUTSIDE",
                letter,
                messages);

            if (outside != null)
                return outside;

            messages?.Add("END-CONTROL " + letter + ": both OUTSIDE creation methods failed; trying INSIDE face fallback now.");

            return TryControlledEnd(
                analysis,
                source,
                target,
                startEnd,
                assemblyParts,
                allowedIds,
                horizontal,
                targetOnLowSide,
                insideCut,
                true,
                "INSIDE FALLBACK",
                letter,
                messages);
        }

        private DrawingView TryControlledEnd(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            ModelPart target,
            bool startEnd,
            IList<ModelPart> assemblyParts,
            ISet<int> allowedIds,
            bool horizontal,
            bool targetOnLowSide,
            double cut,
            bool insideFallback,
            string attempt,
            string letter,
            IList<string> messages)
        {
            Point lineStart;
            Point lineEnd;
            BuildOutsideInSectionLine(source.MainPartBounds, cut, horizontal, targetOnLowSide, out lineStart, out lineEnd);

            var targetSourceBox = GetPartBox(target, source.View.DisplayCoordinateSystem);
            var localOrigin = horizontal
                ? new Point(cut, targetSourceBox.Centre.Y, targetSourceBox.Centre.Z)
                : new Point(targetSourceBox.Centre.X, cut, targetSourceBox.Centre.Z);
            var globalOrigin = SourceLocalToGlobal(source.View.DisplayCoordinateSystem, localOrigin);
            var endCs = BuildEndCoordinateSystem(source.View.DisplayCoordinateSystem, globalOrigin, horizontal, targetOnLowSide);
            var selection = BuildEndZoneSelection(assemblyParts, target, endCs, insideFallback);
            var insertion = GetInsertionPoint(source.View, startEnd);

            messages?.Add("END-CONTROL " + letter + " " + attempt + ": mark line " + P(lineStart) + " -> " + P(lineEnd));
            messages?.Add("END-CONTROL " + letter + " " + attempt + ": view origin global=" + P(globalOrigin));
            messages?.Add("END-CONTROL " + letter + " " + attempt + ": end CS=" + Cs(endCs));
            messages?.Add("END-CONTROL " + letter + " " + attempt + ": restriction min=" + P(selection.Restriction.MinPoint) +
                          " max=" + P(selection.Restriction.MaxPoint));
            messages?.Add("END-CONTROL " + letter + " " + attempt + ": end-zone assembly IDs=" +
                          string.Join(",", selection.PartIdentifiers.Cast<Identifier>().Select(id => id.ID.ToString())) + ".");

            // Method 1: model-area view. This gives us explicit control of the view volume.
            var areaView = CreateAreaView(
                analysis,
                source,
                startEnd,
                endCs,
                selection.Restriction,
                insertion,
                letter,
                attempt,
                messages);

            if (areaView != null)
            {
                LogViewContents(areaView, target, letter, attempt + " AREA", messages);
                if (ContainsTarget(areaView, target.Identifier))
                    return FinaliseSuccessfulView(analysis, source, areaView, lineStart, lineEnd, startEnd, allowedIds, letter, attempt + " AREA", messages);

                areaView.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add("END-CONTROL " + letter + " " + attempt + " AREA: target absent; deleted.");
            }

            // Method 2: exact part-list view. Still the same outside/inside viewing direction,
            // but Tekla is explicitly told which assembly parts belong in this view.
            var partView = CreatePartListView(
                analysis,
                source,
                startEnd,
                endCs,
                selection,
                insertion,
                letter,
                attempt,
                messages);

            if (partView != null)
            {
                LogViewContents(partView, target, letter, attempt + " PART-LIST", messages);
                if (ContainsTarget(partView, target.Identifier))
                    return FinaliseSuccessfulView(analysis, source, partView, lineStart, lineEnd, startEnd, allowedIds, letter, attempt + " PART-LIST", messages);

                partView.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add("END-CONTROL " + letter + " " + attempt + " PART-LIST: target absent; deleted.");
            }

            return null;
        }

        private DrawingView CreateAreaView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            bool startEnd,
            CoordinateSystem endCs,
            AABB restriction,
            Point insertion,
            string letter,
            string attempt,
            IList<string> messages)
        {
            var view = new DrawingView(analysis.Drawing.GetSheet(), endCs, endCs, restriction)
            {
                Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B",
                Attributes = CreateViewAttributes(source.View),
                Origin = insertion
            };

            var ok = view.Insert();
            messages?.Add("END-CONTROL " + letter + " " + attempt + " AREA: Insert=" + ok + ".");
            if (!ok)
                return null;

            analysis.Drawing.CommitChanges();
            return view;
        }

        private DrawingView CreatePartListView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            bool startEnd,
            CoordinateSystem endCs,
            EndZoneSelection selection,
            Point insertion,
            string letter,
            string attempt,
            IList<string> messages)
        {
            var view = new DrawingView(analysis.Drawing.GetSheet(), endCs, endCs, selection.PartIdentifiers)
            {
                Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B",
                Attributes = CreateViewAttributes(source.View),
                RestrictionBox = selection.Restriction,
                Origin = insertion
            };

            var ok = view.Insert();
            messages?.Add("END-CONTROL " + letter + " " + attempt + " PART-LIST: Insert=" + ok + ".");
            if (!ok)
                return null;

            analysis.Drawing.CommitChanges();
            return view;
        }

        private DrawingView FinaliseSuccessfulView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            DrawingView view,
            Point lineStart,
            Point lineEnd,
            bool startEnd,
            ISet<int> allowedIds,
            string letter,
            string route,
            IList<string> messages)
        {
            CleanGeneratedView(view, allowedIds);
            PlaceEndView(analysis.Drawing, source.View, view, startEnd);
            view.Modify();

            var markAttributes = new SectionMarkBase.SectionMarkAttributes { MarkName = letter };
            var mark = new SectionMark(source.View, lineStart, lineEnd, markAttributes);
            var markInserted = mark.Insert();

            analysis.Drawing.CommitChanges();
            messages?.Add("END-CONTROL " + letter + ": SUCCESS via " + route +
                          ". Section mark Insert=" + markInserted + ". Kept outside-in end view.");
            return view;
        }

        private EndZoneSelection BuildEndZoneSelection(
            IList<ModelPart> assemblyParts,
            ModelPart target,
            CoordinateSystem endCs,
            bool insideFallback)
        {
            var minZ = insideFallback ? -100.0 : -BackDepth;
            var maxZ = EndViewDepth;
            var selected = new List<Tuple<ModelPart, PartBox>>();

            foreach (var part in assemblyParts)
            {
                var box = GetPartBox(part, endCs);
                if (box.Max.Z < minZ || box.Min.Z > maxZ)
                    continue;

                selected.Add(Tuple.Create(part, box));
            }

            if (selected.All(item => item.Item1.Identifier.ID != target.Identifier.ID))
                selected.Add(Tuple.Create(target, GetPartBox(target, endCs)));

            var minX = selected.Min(item => item.Item2.Min.X) - CrossSectionMargin;
            var maxX = selected.Max(item => item.Item2.Max.X) + CrossSectionMargin;
            var minY = selected.Min(item => item.Item2.Min.Y) - CrossSectionMargin;
            var maxY = selected.Max(item => item.Item2.Max.Y) + CrossSectionMargin;

            var targetBox = GetPartBox(target, endCs);
            minZ = Math.Min(minZ, targetBox.Min.Z - 5.0);
            maxZ = Math.Max(maxZ, targetBox.Max.Z + 5.0);

            var ids = new ArrayList();
            foreach (var item in selected)
                ids.Add(item.Item1.Identifier);

            return new EndZoneSelection
            {
                Restriction = new AABB(new Point(minX, minY, minZ), new Point(maxX, maxY, maxZ)),
                PartIdentifiers = ids
            };
        }

        private static CoordinateSystem BuildEndCoordinateSystem(
            CoordinateSystem sourceCs,
            Point origin,
            bool horizontal,
            bool targetOnLowSide)
        {
            var sourceX = Normalize(new Vector(sourceCs.AxisX));
            var sourceY = Normalize(new Vector(sourceCs.AxisY));
            var inward = horizontal
                ? (targetOnLowSide ? sourceX : Multiply(sourceX, -1.0))
                : (targetOnLowSide ? sourceY : Multiply(sourceY, -1.0));

            // Prefer model/global vertical on the end view so the fabricated end plate is
            // presented upright. If the member itself is vertical, fall back to the source
            // view's other axis.
            var globalUp = new Vector(0.0, 0.0, 1.0);
            var projectedUp = Subtract(globalUp, Multiply(inward, Dot(globalUp, inward)));
            var axisY = Length(projectedUp) > 0.1
                ? Normalize(projectedUp)
                : Normalize(horizontal ? sourceY : sourceX);
            var axisX = Normalize(Cross(axisY, inward));

            return new CoordinateSystem(origin, axisX, axisY);
        }

        private static Point SourceLocalToGlobal(CoordinateSystem sourceCs, Point local)
        {
            var x = Normalize(new Vector(sourceCs.AxisX));
            var y = Normalize(new Vector(sourceCs.AxisY));
            var z = Normalize(Cross(x, y));

            return new Point(
                sourceCs.Origin.X + x.X * local.X + y.X * local.Y + z.X * local.Z,
                sourceCs.Origin.Y + x.Y * local.X + y.Y * local.Y + z.Y * local.Z,
                sourceCs.Origin.Z + x.Z * local.X + y.Z * local.Y + z.Z * local.Z);
        }

        private static void BuildOutsideInSectionLine(
            ViewBounds bounds,
            double cut,
            bool horizontal,
            bool targetOnLowSide,
            out Point start,
            out Point end)
        {
            if (horizontal)
            {
                var margin = Math.Max(40.0, Math.Abs(bounds.Height) * 0.25);
                var bottom = new Point(cut, bounds.MinY - margin, 0.0);
                var top = new Point(cut, bounds.MaxY + margin, 0.0);
                if (targetOnLowSide)
                {
                    start = bottom;
                    end = top;
                }
                else
                {
                    start = top;
                    end = bottom;
                }
                return;
            }

            var sideMargin = Math.Max(40.0, Math.Abs(bounds.Width) * 0.25);
            var left = new Point(bounds.MinX - sideMargin, cut, 0.0);
            var right = new Point(bounds.MaxX + sideMargin, cut, 0.0);
            if (targetOnLowSide)
            {
                start = right;
                end = left;
            }
            else
            {
                start = left;
                end = right;
            }
        }

        private void LogViewContents(DrawingView view, ModelPart target, string letter, string route, IList<string> messages)
        {
            messages?.Add("END-CONTROL " + letter + " " + route + ": frame=" + F(view.Width) + "x" + F(view.Height) +
                          "; DisplayCS=" + Cs(view.DisplayCoordinateSystem));
            messages?.Add("END-CONTROL " + letter + " " + route + ": RestrictionBox=" +
                          P(view.RestrictionBox.MinPoint) + " -> " + P(view.RestrictionBox.MaxPoint));

            var targetBox = GetPartBox(target, view.DisplayCoordinateSystem);
            messages?.Add("END-CONTROL " + letter + " " + route + ": target in end CS=" + P(targetBox.Min) + " -> " + P(targetBox.Max));

            var typeCounts = new Dictionary<string, int>();
            var ids = new List<string>();
            var objects = view.GetModelObjects();
            while (objects != null && objects.MoveNext())
            {
                var current = objects.Current;
                var typeName = current == null ? "<null>" : current.GetType().Name;
                int count;
                typeCounts.TryGetValue(typeName, out count);
                typeCounts[typeName] = count + 1;

                var modelObject = current as DrawingModelObject;
                if (modelObject != null && modelObject.ModelIdentifier != null && ids.Count < 25)
                    ids.Add(typeName + ":" + modelObject.ModelIdentifier.ID);
            }

            messages?.Add("END-CONTROL " + letter + " " + route + ": model object types=" +
                          (typeCounts.Count == 0 ? "<none>" : string.Join(", ", typeCounts.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value))) + ".");
            messages?.Add("END-CONTROL " + letter + " " + route + ": first model IDs=" +
                          (ids.Count == 0 ? "<none>" : string.Join(", ", ids)) + ".");
            messages?.Add("END-CONTROL " + letter + " " + route + ": target present=" + ContainsTarget(view, target.Identifier) + ".");
        }

        private static bool ContainsTarget(DrawingView view, Identifier target)
        {
            var exact = view.GetModelObjects(target);
            if (exact != null && exact.MoveNext())
                return true;

            var all = view.GetModelObjects();
            while (all != null && all.MoveNext())
            {
                var modelObject = all.Current as DrawingModelObject;
                if (modelObject != null && modelObject.ModelIdentifier != null && modelObject.ModelIdentifier.ID == target.ID)
                    return true;
            }

            return false;
        }

        private static DrawingView.ViewAttributes CreateViewAttributes(DrawingView source)
        {
            var attributes = new DrawingView.ViewAttributes();
            if (source != null && source.Attributes != null)
            {
                if (source.Attributes.Scale > 0.0)
                    attributes.Scale = source.Attributes.Scale;
                attributes.Shortening = source.Attributes.Shortening;
            }

            attributes.FixedViewPlacing = true;
            attributes.ViewExtensionForNeighbourParts = 0.0;
            attributes.TagsAttributes = new DrawingView.ViewMarkTagsAttributes();
            return attributes;
        }

        private static void CleanGeneratedView(DrawingView view, ISet<int> allowedIds)
        {
            if (view == null)
                return;

            var modelObjects = view.GetModelObjects();
            while (modelObjects != null && modelObjects.MoveNext())
            {
                var modelObject = modelObjects.Current as DrawingModelObject;
                if (modelObject == null || modelObject.ModelIdentifier == null)
                    continue;

                if (allowedIds.Contains(modelObject.ModelIdentifier.ID))
                    continue;

                if (modelObject.Hideable != null)
                {
                    modelObject.Hideable.HideFromDrawingView();
                    modelObject.Modify();
                }
            }

            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid == null)
                    continue;

                if (grid.Hideable != null)
                    grid.Hideable.HideFromDrawingView();

                var lines = grid.GetObjects();
                while (lines.MoveNext())
                {
                    var line = lines.Current as DrawingGridLine;
                    if (line != null && line.Hideable != null)
                        line.Hideable.HideFromDrawingView();
                }
            }

            var allGridLines = view.GetAllObjects(new[] { typeof(DrawingGridLine) });
            while (allGridLines.MoveNext())
            {
                var line = allGridLines.Current as DrawingGridLine;
                if (line != null && line.Hideable != null)
                    line.Hideable.HideFromDrawingView();
            }

            var marks = view.GetObjects(new[] { typeof(DrawingMark), typeof(DrawingWeldMark) });
            var delete = new List<DrawingObject>();
            while (marks.MoveNext())
            {
                var item = marks.Current as DrawingObject;
                if (item != null)
                    delete.Add(item);
            }
            foreach (var item in delete)
                item.Delete();
        }

        private static void PlaceEndView(Drawing drawing, DrawingView source, DrawingView view, bool startEnd)
        {
            var horizontal = source.Width * 0.5 + view.Width * 0.5 + ViewGap;
            var desired = new Point(source.Origin.X + (startEnd ? -horizontal : horizontal), source.Origin.Y, 0.0);
            var sheet = drawing.GetSheet();
            if (sheet != null && sheet.Width > 0.0 && sheet.Height > 0.0)
            {
                var hw = view.Width * 0.5 + ViewGap;
                var hh = view.Height * 0.5 + ViewGap;
                desired.X = Math.Max(hw, Math.Min(sheet.Width - hw, desired.X));
                desired.Y = Math.Max(hh, Math.Min(sheet.Height - hh, desired.Y));
            }
            view.Origin = desired;
        }

        private static Point GetInsertionPoint(DrawingView source, bool startEnd)
        {
            var horizontal = source.Width * 0.5 + 30.0;
            return new Point(source.Origin.X + (startEnd ? -horizontal : horizontal), source.Origin.Y, 0.0);
        }

        private EndDetection DetectEndPlates(ModelPart mainPart, IList<ModelPart> parts, IList<string> messages)
        {
            var result = new EndDetection();
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();
            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(mainPart.GetCoordinateSystem()));
                var main = mainPart.GetSolid();
                var minX = main.MinimumPoint.X;
                var maxX = main.MaximumPoint.X;
                var length = Math.Abs(maxX - minX);
                var endZone = Math.Max(100.0, Math.Min(400.0, length * 0.04));
                messages?.Add("END-CONTROL detection: main local X " + F(minX) + " .. " + F(maxX) + "; zone=" + F(endZone) + ".");

                foreach (var part in parts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID || !IsPlateLike(part))
                        continue;

                    var solid = part.GetSolid();
                    var sx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                    var sy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                    var sz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
                    var transverse = Math.Max(sy, sz);
                    var cx = (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5;
                    var dA = Math.Abs(cx - minX);
                    var dB = Math.Abs(cx - maxX);
                    var transversePlate = sx <= Math.Max(60.0, transverse * 0.65);
                    var tolerance = endZone + sx * 0.5;
                    var atA = transversePlate && dA <= tolerance;
                    var atB = transversePlate && dB <= tolerance;

                    messages?.Add("END-CONTROL candidate " + Describe(part) + ": X[" + F(solid.MinimumPoint.X) + "," + F(solid.MaximumPoint.X) +
                                  "] spanX=" + F(sx) + " dA=" + F(dA) + " dB=" + F(dB) + " => A=" + atA + ", B=" + atB);

                    if (atA && (result.Start == null || dA < result.Start.Distance))
                        result.Start = new Candidate(part, dA);
                    if (atB && (result.Finish == null || dB < result.Finish.Distance))
                        result.Finish = new Candidate(part, dB);
                }
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }

            messages?.Add("END-CONTROL selected A=" + (result.Start == null ? "<none>" : Describe(result.Start.Part)) +
                          "; B=" + (result.Finish == null ? "<none>" : Describe(result.Finish.Part)) + ".");
            return result;
        }

        private PartBox GetPartBox(ModelPart part, CoordinateSystem cs)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();
            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(cs));
                var solid = part.GetSolid();
                return new PartBox(new Point(solid.MinimumPoint), new Point(solid.MaximumPoint));
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static ViewAnalysis GetBaseView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null && !IsGenerated(view.View))
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                    .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                    .FirstOrDefault();
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null)
                return result;

            foreach (var item in assembly.GetSecondaries())
            {
                var part = item as ModelPart;
                if (part != null)
                    result.Add(part);
            }
            return result;
        }

        private static bool IsPlateLike(ModelPart part)
        {
            if (part is ContourPlate)
                return true;

            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            if (string.IsNullOrWhiteSpace(profile))
                return false;

            var value = profile.Trim().ToUpperInvariant();
            return value.StartsWith("PL") || value.StartsWith("PLT") || value.StartsWith("PLATE");
        }

        private static void DeleteGeneratedEnd(Drawing drawing, string name)
        {
            if (drawing == null)
                return;

            var views = drawing.GetSheet().GetAllViews();
            var delete = new List<DrawingView>();
            while (views.MoveNext())
            {
                var view = views.Current as DrawingView;
                if (view != null && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                    delete.Add(view);
            }
            foreach (var view in delete)
                view.Delete();
        }

        private static void RemoveExistingSectionMark(DrawingView source, string letter)
        {
            if (source == null)
                return;

            // We cannot reliably identify a mark's displayed text in all environments,
            // so only remove marks created in previous tool runs by deleting all section
            // marks from the retained base view while view creation is still in prototype mode.
            var marks = source.GetObjects(new[] { typeof(SectionMark) });
            var delete = new List<SectionMark>();
            while (marks.MoveNext())
            {
                var mark = marks.Current as SectionMark;
                if (mark != null)
                    delete.Add(mark);
            }
            foreach (var mark in delete)
                mark.Delete();
        }

        private static bool IsGenerated(DrawingView view)
        {
            var name = (view == null ? string.Empty : view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name.StartsWith("AUTO -");
        }

        private static DrawingView.ViewAttributes CreateViewAttributes(DrawingView source)
        {
            var attributes = new DrawingView.ViewAttributes();
            if (source != null && source.Attributes != null)
            {
                if (source.Attributes.Scale > 0.0)
                    attributes.Scale = source.Attributes.Scale;
                attributes.Shortening = source.Attributes.Shortening;
            }
            attributes.FixedViewPlacing = true;
            attributes.ViewExtensionForNeighbourParts = 0.0;
            attributes.TagsAttributes = new DrawingView.ViewMarkTagsAttributes();
            return attributes;
        }

        private static string Describe(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return part.Identifier.ID + (string.IsNullOrWhiteSpace(profile) ? string.Empty : " (" + profile + ")");
        }

        private static string F(double value) { return value.ToString("0.###"); }
        private static string P(Point point) { return point == null ? "<null>" : "(" + F(point.X) + ", " + F(point.Y) + ", " + F(point.Z) + ")"; }
        private static string Cs(CoordinateSystem cs)
        {
            if (cs == null) return "<null>";
            return "O=" + P(cs.Origin) + " X=" + V(cs.AxisX) + " Y=" + V(cs.AxisY) + " N=" + V(Cross(new Vector(cs.AxisX), new Vector(cs.AxisY)));
        }
        private static string V(Vector v) { return "(" + F(v.X) + ", " + F(v.Y) + ", " + F(v.Z) + ")"; }

        private static Vector Cross(Vector a, Vector b)
        {
            return new Vector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        private static double Dot(Vector a, Vector b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static double Length(Vector v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }
        private static Vector Normalize(Vector v)
        {
            var length = Length(v);
            return length < 0.000001 ? new Vector() : new Vector(v.X / length, v.Y / length, v.Z / length);
        }
        private static Vector Multiply(Vector v, double value) { return new Vector(v.X * value, v.Y * value, v.Z * value); }
        private static Vector Subtract(Vector a, Vector b) { return new Vector(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        private sealed class EndDetection
        {
            public Candidate Start { get; set; }
            public Candidate Finish { get; set; }
        }

        private sealed class Candidate
        {
            public Candidate(ModelPart part, double distance)
            {
                Part = part;
                Distance = distance;
            }
            public ModelPart Part { get; }
            public double Distance { get; }
        }

        private sealed class EndZoneSelection
        {
            public AABB Restriction { get; set; }
            public ArrayList PartIdentifiers { get; set; }
        }

        private sealed class PartBox
        {
            public PartBox(Point min, Point max)
            {
                Min = min;
                Max = max;
            }
            public Point Min { get; }
            public Point Max { get; }
            public Point Centre => new Point((Min.X + Max.X) * 0.5, (Min.Y + Max.Y) * 0.5, (Min.Z + Max.Z) * 0.5);
        }
    }
}
