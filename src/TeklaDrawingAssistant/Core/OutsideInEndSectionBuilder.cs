using System;
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
using DrawingPart = Tekla.Structures.Drawing.Part;
using DrawingView = Tekla.Structures.Drawing.View;
using DrawingWeldMark = Tekla.Structures.Drawing.WeldMark;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Creates fabrication end sections from the outside looking into the member.
    /// The outside plate face is always attempted first. Only if that fails is the
    /// inside plate face attempted. This class is the only authority for end sections.
    /// </summary>
    public sealed class OutsideInEndSectionBuilder
    {
        private const double ViewGap = 12.0;
        private const double SectionDepth = 1500.0;
        private readonly Model _model;

        public OutsideInEndSectionBuilder(Model model)
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
                messages?.Add("END: no usable base view found.");
                return 0;
            }

            messages?.Add("END ======================================================");
            messages?.Add("END policy: OUTSIDE face first, looking inward. INSIDE face is fallback only.");
            messages?.Add("END source main bounds: X " + F(source.MainPartBounds.MinX) + " .. " + F(source.MainPartBounds.MaxX) +
                          ", Y " + F(source.MainPartBounds.MinY) + " .. " + F(source.MainPartBounds.MaxY));
            messages?.Add("END source DisplayCS: " + Cs(source.View.DisplayCoordinateSystem));

            var assemblyParts = GetAssemblyParts(analysis.MainPart);
            var allowedIds = new HashSet<int>(assemblyParts.Select(p => p.Identifier.ID));
            var detection = DetectEndPlates(analysis.MainPart, assemblyParts, messages);
            var created = 0;

            if (detection.Start != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_A");
                if (BuildOneEnd(analysis, source, detection.Start.Part, true, allowedIds, messages) != null)
                    created++;
            }

            if (detection.Finish != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_B");
                if (BuildOneEnd(analysis, source, detection.Finish.Part, false, allowedIds, messages) != null)
                    created++;
            }

            messages?.Add("END ======================================================");
            analysis.Drawing.CommitChanges();
            return created;
        }

        private DrawingView BuildOneEnd(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            ModelPart target,
            bool startEnd,
            ISet<int> assemblyPartIds,
            IList<string> messages)
        {
            var letter = startEnd ? "A" : "B";
            var box = GetPartBox(target, source.View.DisplayCoordinateSystem);
            var horizontal = Math.Abs(source.MainPartBounds.Width) >= Math.Abs(source.MainPartBounds.Height);

            var memberCentre = horizontal
                ? (source.MainPartBounds.MinX + source.MainPartBounds.MaxX) * 0.5
                : (source.MainPartBounds.MinY + source.MainPartBounds.MaxY) * 0.5;
            var targetCentre = horizontal ? box.Centre.X : box.Centre.Y;
            var targetOnLowSide = targetCentre < memberCentre;

            // Outside is the face further away from the member centre; inside is the face facing the member.
            var outsideCut = horizontal
                ? (targetOnLowSide ? box.Min.X : box.Max.X)
                : (targetOnLowSide ? box.Min.Y : box.Max.Y);
            var insideCut = horizontal
                ? (targetOnLowSide ? box.Max.X : box.Min.X)
                : (targetOnLowSide ? box.Max.Y : box.Min.Y);

            messages?.Add("END " + letter + ": target " + Describe(target));
            messages?.Add("END " + letter + ": target source box min=" + P(box.Min) + " max=" + P(box.Max) + " centre=" + P(box.Centre));
            messages?.Add("END " + letter + ": target lies on source " + (targetOnLowSide ? "LOW" : "HIGH") + " side of member.");
            messages?.Add("END " + letter + ": OUTSIDE cut=" + F(outsideCut) + "; INSIDE fallback cut=" + F(insideCut) + ".");

            // Fabrication rule: stand outside the plate and look towards the member centre.
            var outside = TryCreate(
                analysis,
                source,
                target,
                startEnd,
                assemblyPartIds,
                horizontal,
                targetOnLowSide,
                outsideCut,
                "OUTSIDE face",
                letter,
                messages);

            if (outside != null)
                return outside;

            messages?.Add("END " + letter + ": OUTSIDE attempt failed - now trying INSIDE face fallback.");

            return TryCreate(
                analysis,
                source,
                target,
                startEnd,
                assemblyPartIds,
                horizontal,
                targetOnLowSide,
                insideCut,
                "INSIDE face fallback",
                letter,
                messages);
        }

        private DrawingView TryCreate(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            ModelPart target,
            bool startEnd,
            ISet<int> assemblyPartIds,
            bool horizontal,
            bool targetOnLowSide,
            double cut,
            string attemptName,
            string letter,
            IList<string> messages)
        {
            Point lineStart;
            Point lineEnd;
            BuildOutsideInSectionLine(
                source.MainPartBounds,
                cut,
                horizontal,
                targetOnLowSide,
                out lineStart,
                out lineEnd);

            var insertion = GetInsertionPoint(source.View, startEnd);
            messages?.Add("END " + letter + " " + attemptName + ": line " + P(lineStart) + " -> " + P(lineEnd) +
                          "; depthUp/down=" + F(SectionDepth) + "; insertion=" + P(insertion));

            var attributes = CreateViewAttributes(source.View);
            var markAttributes = new SectionMarkBase.SectionMarkAttributes { MarkName = letter };
            DrawingView sectionView;
            SectionMark sectionMark;

            var ok = DrawingView.CreateSectionView(
                source.View,
                lineStart,
                lineEnd,
                insertion,
                SectionDepth,
                SectionDepth,
                attributes,
                markAttributes,
                out sectionView,
                out sectionMark);

            messages?.Add("END " + letter + " " + attemptName + ": CreateSectionView=" + ok +
                          ", viewNull=" + (sectionView == null) + ", markNull=" + (sectionMark == null));

            if (!ok || sectionView == null)
                return null;

            sectionView.Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B";
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            LogCreatedView(sectionView, target, letter, attemptName, messages);

            // Validate two ways. GetObjects<Part> was returning zero in the diagnostic log,
            // so also use the API specifically intended to ask whether a model object is in the view.
            var drawingPartVisible = ContainsDrawingPart(sectionView, target.Identifier.ID);
            var modelObjectVisible = ContainsModelObject(sectionView, target.Identifier);

            messages?.Add("END " + letter + " " + attemptName + ": validation DrawingPart=" + drawingPartVisible +
                          ", GetModelObjects(target)=" + modelObjectVisible + ".");

            if (!drawingPartVisible && !modelObjectVisible)
            {
                if (sectionMark != null)
                    sectionMark.Delete();
                sectionView.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add("END " + letter + " " + attemptName + ": rejected - target not reported in section by either API check.");
                return null;
            }

            CleanSectionView(sectionView, assemblyPartIds);
            PlaceEndSection(analysis.Drawing, source.View, sectionView, startEnd);
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add("END " + letter + " " + attemptName + ": SUCCESS - kept as " + (startEnd ? "A-A" : "B-B") +
                          " looking from outside toward the member.");
            return sectionView;
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

                // In Tekla's section-view convention for a vertical cut line:
                // bottom -> top looks toward +source-X; top -> bottom looks toward -source-X.
                // Low-side end therefore uses bottom->top; high-side end uses top->bottom.
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

            // For a horizontal cut line, right -> left looks toward +source-Y;
            // left -> right looks toward -source-Y.
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

        private EndDetection DetectEndPlates(ModelPart mainPart, IList<ModelPart> assemblyParts, IList<string> messages)
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

                messages?.Add("END detection: main local X " + F(minX) + " .. " + F(maxX) + "; search zone=" + F(endZone) + " mm.");

                foreach (var part in assemblyParts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID || !IsPlateLike(part))
                        continue;

                    var solid = part.GetSolid();
                    var sx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                    var sy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                    var sz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
                    var transverse = Math.Max(sy, sz);
                    var centreX = (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5;
                    var dA = Math.Abs(centreX - minX);
                    var dB = Math.Abs(centreX - maxX);
                    var tolerance = endZone + sx * 0.5;
                    var transversePlate = sx <= Math.Max(60.0, transverse * 0.65);
                    var atA = transversePlate && dA <= tolerance;
                    var atB = transversePlate && dB <= tolerance;

                    messages?.Add("END candidate " + Describe(part) + ": X[" + F(solid.MinimumPoint.X) + "," + F(solid.MaximumPoint.X) +
                                  "] spanX=" + F(sx) + " transverse=" + F(transverse) + " dA=" + F(dA) + " dB=" + F(dB) +
                                  " => A=" + atA + ", B=" + atB);

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

            messages?.Add("END selected A=" + (result.Start == null ? "<none>" : Describe(result.Start.Part)) +
                          "; B=" + (result.Finish == null ? "<none>" : Describe(result.Finish.Part)) + ".");
            return result;
        }

        private void LogCreatedView(DrawingView view, ModelPart target, string letter, string attempt, IList<string> messages)
        {
            messages?.Add("END " + letter + " " + attempt + ": frame=" + F(view.Width) + "x" + F(view.Height) +
                          " scale=" + F(view.Attributes == null ? 0.0 : view.Attributes.Scale));
            messages?.Add("END " + letter + " " + attempt + ": DisplayCS=" + Cs(view.DisplayCoordinateSystem));
            if (view.RestrictionBox != null)
                messages?.Add("END " + letter + " " + attempt + ": RestrictionBox min=" + P(view.RestrictionBox.MinPoint) +
                              " max=" + P(view.RestrictionBox.MaxPoint));

            var targetBox = GetPartBox(target, view.DisplayCoordinateSystem);
            messages?.Add("END " + letter + " " + attempt + ": target in created DisplayCS min=" + P(targetBox.Min) +
                          " max=" + P(targetBox.Max) + " centre=" + P(targetBox.Centre));

            var allModelObjects = view.GetModelObjects();
            var modelCount = 0;
            while (allModelObjects != null && allModelObjects.MoveNext())
                modelCount++;
            messages?.Add("END " + letter + " " + attempt + ": view.GetModelObjects() count=" + modelCount + ".");

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            var ids = new List<string>();
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part != null && part.ModelIdentifier != null)
                    ids.Add(part.ModelIdentifier.ID.ToString());
            }
            messages?.Add("END " + letter + " " + attempt + ": DrawingPart count=" + ids.Count +
                          (ids.Count == 0 ? " <none>" : " ids=" + string.Join(",", ids)) + ".");
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

        private static bool ContainsDrawingPart(DrawingView view, int modelId)
        {
            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part != null && part.ModelIdentifier != null && part.ModelIdentifier.ID == modelId)
                    return true;
            }
            return false;
        }

        private static bool ContainsModelObject(DrawingView view, Identifier identifier)
        {
            var objects = view.GetModelObjects(identifier);
            return objects != null && objects.MoveNext();
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

        private static void CleanSectionView(DrawingView view, ISet<int> assemblyPartIds)
        {
            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid == null) continue;
                if (grid.Hideable != null) grid.Hideable.HideFromDrawingView();
                var lines = grid.GetObjects();
                while (lines.MoveNext())
                {
                    var line = lines.Current as DrawingGridLine;
                    if (line != null && line.Hideable != null) line.Hideable.HideFromDrawingView();
                }
            }

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part == null || part.ModelIdentifier == null || assemblyPartIds.Contains(part.ModelIdentifier.ID)) continue;
                if (part.Hideable != null) part.Hideable.HideFromDrawingView();
            }

            var marks = view.GetObjects(new[] { typeof(DrawingMark), typeof(DrawingWeldMark) });
            var delete = new List<DrawingObject>();
            while (marks.MoveNext())
            {
                var item = marks.Current as DrawingObject;
                if (item != null) delete.Add(item);
            }
            foreach (var item in delete) item.Delete();
        }

        private static void PlaceEndSection(Drawing drawing, DrawingView source, DrawingView view, bool startEnd)
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

        private static void DeleteGeneratedSection(Drawing drawing, string name)
        {
            if (drawing == null) return;
            var views = drawing.GetSheet().GetAllViews();
            var delete = new List<DrawingView>();
            while (views.MoveNext())
            {
                var view = views.Current as DrawingView;
                if (view != null && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase)) delete.Add(view);
            }
            foreach (var view in delete) view.Delete();
        }

        private static ViewAnalysis GetBaseView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(v => v.View != null && v.ContainsMainPart && v.MainPartBounds != null)
                .Where(v => !IsGenerated(v.View))
                .OrderByDescending(v => Math.Abs(v.MainPartBounds.Width * v.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(v => v.View != null && v.ContainsMainPart && v.MainPartBounds != null)
                    .OrderByDescending(v => Math.Abs(v.MainPartBounds.Width * v.MainPartBounds.Height))
                    .FirstOrDefault();
        }

        private static bool IsGenerated(DrawingView view)
        {
            var name = (view == null ? string.Empty : view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name.StartsWith("AUTO -");
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null) return result;
            foreach (var item in assembly.GetSecondaries())
            {
                var part = item as ModelPart;
                if (part != null) result.Add(part);
            }
            return result;
        }

        private static bool IsPlateLike(ModelPart part)
        {
            if (part is ContourPlate) return true;
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            if (string.IsNullOrWhiteSpace(profile)) return false;
            var value = profile.Trim().ToUpperInvariant();
            return value.StartsWith("PL") || value.StartsWith("PLT") || value.StartsWith("PLATE");
        }

        private static string Describe(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return part.Identifier.ID + (string.IsNullOrWhiteSpace(profile) ? string.Empty : " (" + profile + ")");
        }

        private static string F(double value) { return value.ToString("0.###"); }
        private static string P(Point p) { return p == null ? "<null>" : "(" + F(p.X) + ", " + F(p.Y) + ", " + F(p.Z) + ")"; }
        private static string V(Vector v) { return v == null ? "<null>" : "(" + F(v.X) + ", " + F(v.Y) + ", " + F(v.Z) + ")"; }
        private static string Cs(CoordinateSystem cs)
        {
            if (cs == null) return "<null>";
            var n = GeometryMath.Cross(new Vector(cs.AxisX), new Vector(cs.AxisY));
            return "O=" + P(cs.Origin) + " X=" + V(new Vector(cs.AxisX)) + " Y=" + V(new Vector(cs.AxisY)) + " N=" + V(n);
        }

        private sealed class EndDetection
        {
            public Candidate Start { get; set; }
            public Candidate Finish { get; set; }
        }

        private sealed class Candidate
        {
            public Candidate(ModelPart part, double distance) { Part = part; Distance = distance; }
            public ModelPart Part { get; private set; }
            public double Distance { get; private set; }
        }

        private sealed class PartBox
        {
            public PartBox(Point min, Point max)
            {
                Min = min;
                Max = max;
                Centre = new Point((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5);
            }
            public Point Min { get; private set; }
            public Point Max { get; private set; }
            public Point Centre { get; private set; }
        }
    }
}
