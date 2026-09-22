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
    /// End-view creation kept deliberately simple:
    /// 1. Find the physical end plate.
    /// 2. Find the outside face of that plate in the retained base view.
    /// 3. Create a normal Tekla section from that face, looking inward.
    /// 4. Use the base view attributes, so scale/representation match the main view.
    /// 5. Keep the section if Tekla says CreateSectionView succeeded. API visibility checks
    ///    are logged only; they no longer cause a geometrically valid section to be deleted.
    ///
    /// Only if the OUTSIDE face call itself fails do we try the INSIDE plate face.
    /// </summary>
    public sealed class ControlledEndViewBuilder
    {
        private const double ViewGap = 12.0;
        private const double SectionDepth = 1000.0;
        private const double SectionMargin = 50.0;

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
                messages?.Add("END: no usable base view found.");
                return 0;
            }

            var assemblyParts = GetAssemblyParts(analysis.MainPart);
            var allowedIds = new HashSet<int>(assemblyParts.Select(part => part.Identifier.ID));
            var detection = DetectEndPlates(analysis.MainPart, assemblyParts, messages);
            var created = 0;

            messages?.Add("END ======================================================");
            messages?.Add("END strategy: standard Tekla CreateSectionView only.");
            messages?.Add("END policy: OUTSIDE plate face first, looking inward; INSIDE face only if section creation itself fails.");
            messages?.Add("END section scale/representation: copied from retained base view.");

            if (detection.Start != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_A");
                RemoveExistingSectionMarks(source.View);

                if (BuildOneEnd(
                    analysis,
                    source,
                    detection.Start.Part,
                    true,
                    allowedIds,
                    messages) != null)
                {
                    created++;
                }
            }

            if (detection.Finish != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_B");

                if (BuildOneEnd(
                    analysis,
                    source,
                    detection.Finish.Part,
                    false,
                    allowedIds,
                    messages) != null)
                {
                    created++;
                }
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
            ISet<int> allowedIds,
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

            var outsideCut = horizontal
                ? (targetOnLowSide ? box.Min.X : box.Max.X)
                : (targetOnLowSide ? box.Min.Y : box.Max.Y);

            var insideCut = horizontal
                ? (targetOnLowSide ? box.Max.X : box.Min.X)
                : (targetOnLowSide ? box.Max.Y : box.Min.Y);

            messages?.Add("END " + letter + ": target " + Describe(target));
            messages?.Add("END " + letter + ": target source box " + P(box.Min) + " -> " + P(box.Max));
            messages?.Add("END " + letter + ": target is on " + (targetOnLowSide ? "LOW" : "HIGH") + " side of the member.");
            messages?.Add("END " + letter + ": OUTSIDE cut=" + F(outsideCut) + "; INSIDE fallback cut=" + F(insideCut) + ".");

            var outside = TryCreateSection(
                analysis,
                source,
                target,
                startEnd,
                allowedIds,
                horizontal,
                targetOnLowSide,
                outsideCut,
                "OUTSIDE",
                letter,
                messages);

            if (outside != null)
                return outside;

            messages?.Add("END " + letter + ": OUTSIDE CreateSectionView failed; trying INSIDE face.");

            return TryCreateSection(
                analysis,
                source,
                target,
                startEnd,
                allowedIds,
                horizontal,
                targetOnLowSide,
                insideCut,
                "INSIDE",
                letter,
                messages);
        }

        private DrawingView TryCreateSection(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            ModelPart target,
            bool startEnd,
            ISet<int> allowedIds,
            bool horizontal,
            bool targetOnLowSide,
            double cut,
            string attempt,
            string letter,
            IList<string> messages)
        {
            var targetBox = GetPartBox(target, source.View.DisplayCoordinateSystem);

            Point lineStart;
            Point lineEnd;
            BuildSectionLine(
                source.MainPartBounds,
                targetBox,
                cut,
                horizontal,
                targetOnLowSide,
                out lineStart,
                out lineEnd);

            var insertion = GetInsertionPoint(source.View, startEnd);
            var markAttributes = new SectionMarkBase.SectionMarkAttributes
            {
                MarkName = letter
            };

            var viewAttributes = source.View.Attributes ?? new DrawingView.ViewAttributes();

            messages?.Add(
                "END " + letter + " " + attempt + ": section line " + P(lineStart) + " -> " + P(lineEnd) +
                "; insertion=" + P(insertion) +
                "; source scale=" + F(viewAttributes.Scale) +
                "; depthUp/down=" + F(SectionDepth) + ".");

            DrawingView sectionView;
            SectionMark sectionMark;

            var created = DrawingView.CreateSectionView(
                source.View,
                lineStart,
                lineEnd,
                insertion,
                SectionDepth,
                SectionDepth,
                viewAttributes,
                markAttributes,
                out sectionView,
                out sectionMark);

            messages?.Add(
                "END " + letter + " " + attempt + ": CreateSectionView=" + created +
                ", viewNull=" + (sectionView == null) +
                ", markNull=" + (sectionMark == null) + ".");

            if (!created || sectionView == null)
                return null;

            sectionView.Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B";
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            PlaceEndSection(analysis.Drawing, source.View, sectionView, startEnd);
            CleanGeneratedSection(sectionView, allowedIds);
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            var drawingPartVisible = ContainsDrawingPart(sectionView, target.Identifier.ID);
            var modelObjectVisible = ContainsModelObject(sectionView, target.Identifier);
            var drawingPartCount = CountDrawingParts(sectionView);

            messages?.Add(
                "END " + letter + " " + attempt + ": kept section. frame=" +
                F(sectionView.Width) + "x" + F(sectionView.Height) +
                "; scale=" + F(sectionView.Attributes == null ? 0.0 : sectionView.Attributes.Scale) +
                "; DrawingPart count=" + drawingPartCount +
                "; target via DrawingPart=" + drawingPartVisible +
                "; target via GetModelObjects=" + modelObjectVisible +
                " (diagnostic only).");

            messages?.Add(
                "END " + letter + " " + attempt + ": SUCCESS - section kept and placed " +
                (startEnd ? "left" : "right") + " of the base view.");

            return sectionView;
        }

        private static void BuildSectionLine(
            ViewBounds mainBounds,
            PartBox targetBounds,
            double cut,
            bool horizontal,
            bool targetOnLowSide,
            out Point start,
            out Point end)
        {
            if (horizontal)
            {
                var minY = Math.Min(mainBounds.MinY, targetBounds.Min.Y) - SectionMargin;
                var maxY = Math.Max(mainBounds.MaxY, targetBounds.Max.Y) + SectionMargin;
                var bottom = new Point(cut, minY, 0.0);
                var top = new Point(cut, maxY, 0.0);

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

            var minX = Math.Min(mainBounds.MinX, targetBounds.Min.X) - SectionMargin;
            var maxX = Math.Max(mainBounds.MaxX, targetBounds.Max.X) + SectionMargin;
            var left = new Point(minX, cut, 0.0);
            var right = new Point(maxX, cut, 0.0);

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

        private EndDetection DetectEndPlates(
            ModelPart mainPart,
            IList<ModelPart> assemblyParts,
            IList<string> messages)
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

                messages?.Add("END detection: main local X " + F(minX) + " .. " + F(maxX) +
                              "; search zone=" + F(endZone) + " mm.");

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

                    messages?.Add(
                        "END candidate " + Describe(part) +
                        ": X[" + F(solid.MinimumPoint.X) + "," + F(solid.MaximumPoint.X) + "]" +
                        " spanX=" + F(sx) +
                        " transverse=" + F(transverse) +
                        " dA=" + F(dA) +
                        " dB=" + F(dB) +
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

            messages?.Add(
                "END selected A=" + (result.Start == null ? "<none>" : Describe(result.Start.Part)) +
                "; B=" + (result.Finish == null ? "<none>" : Describe(result.Finish.Part)) + ".");

            return result;
        }

        private PartBox GetPartBox(ModelPart part, CoordinateSystem coordinateSystem)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(coordinateSystem));
                var solid = part.GetSolid();
                return new PartBox(
                    new Point(solid.MinimumPoint),
                    new Point(solid.MaximumPoint));
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static void CleanGeneratedSection(DrawingView view, ISet<int> allowedIds)
        {
            if (view == null)
                return;

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

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part == null || part.ModelIdentifier == null || allowedIds.Contains(part.ModelIdentifier.ID))
                    continue;

                if (part.Hideable != null)
                    part.Hideable.HideFromDrawingView();
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

        private static int CountDrawingParts(DrawingView view)
        {
            var count = 0;
            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
                count++;
            return count;
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

        private static void PlaceEndSection(
            Drawing drawing,
            DrawingView source,
            DrawingView section,
            bool startEnd)
        {
            var horizontal = source.Width * 0.5 + section.Width * 0.5 + ViewGap;
            var desired = new Point(
                source.Origin.X + (startEnd ? -horizontal : horizontal),
                source.Origin.Y,
                0.0);

            var sheet = drawing.GetSheet();
            if (sheet != null && sheet.Width > 0.0 && sheet.Height > 0.0)
            {
                var halfWidth = section.Width * 0.5 + ViewGap;
                var halfHeight = section.Height * 0.5 + ViewGap;
                desired.X = Math.Max(halfWidth, Math.Min(sheet.Width - halfWidth, desired.X));
                desired.Y = Math.Max(halfHeight, Math.Min(sheet.Height - halfHeight, desired.Y));
            }

            section.Origin = desired;
        }

        private static Point GetInsertionPoint(DrawingView source, bool startEnd)
        {
            var distance = source.Width * 0.5 + 30.0;
            return new Point(
                source.Origin.X + (startEnd ? -distance : distance),
                source.Origin.Y,
                0.0);
        }

        private static void DeleteGeneratedSection(Drawing drawing, string name)
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

        private static void RemoveExistingSectionMarks(DrawingView source)
        {
            if (source == null)
                return;

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

        private static ViewAnalysis GetBaseView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view =>
                    view.View != null &&
                    view.ContainsMainPart &&
                    view.MainPartBounds != null &&
                    !IsGenerated(view.View))
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(view =>
                        view.View != null &&
                        view.ContainsMainPart &&
                        view.MainPartBounds != null)
                    .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
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

        private static string Describe(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return part.Identifier.ID + (string.IsNullOrWhiteSpace(profile) ? string.Empty : " (" + profile + ")");
        }

        private static string F(double value)
        {
            return value.ToString("0.###");
        }

        private static string P(Point point)
        {
            return point == null
                ? "<null>"
                : "(" + F(point.X) + ", " + F(point.Y) + ", " + F(point.Z) + ")";
        }

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

        private sealed class PartBox
        {
            public PartBox(Point min, Point max)
            {
                Min = min;
                Max = max;
                Centre = new Point(
                    (min.X + max.X) * 0.5,
                    (min.Y + max.Y) * 0.5,
                    (min.Z + max.Z) * 0.5);
            }

            public Point Min { get; }
            public Point Max { get; }
            public Point Centre { get; }
        }
    }
}
