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
    /// Temporary diagnostic/rescue pass for end sections.
    /// It deliberately logs enough geometry to tell us whether the problem is:
    /// 1) wrong end-plate selection,
    /// 2) wrong source-view coordinates,
    /// 3) section cut position/depth,
    /// 4) section-view restriction box / visibility settings.
    ///
    /// Failed diagnostic views are deleted. If one of the alternate cut/depth attempts
    /// actually contains the detected end plate, that view is kept as the real end section.
    /// </summary>
    public sealed class EndSectionDiagnosticProbe
    {
        private const double ViewGap = 12.0;
        private readonly Model _model;

        public EndSectionDiagnosticProbe(Model model)
        {
            _model = model;
        }

        public int DiagnoseAndRescue(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null || messages == null)
                return 0;

            var source = GetBaseView(analysis);
            if (source == null || source.MainPartBounds == null)
            {
                messages.Add("END-DIAG: no usable base view found.");
                return 0;
            }

            messages.Add("END-DIAG ==================================================");
            messages.Add("END-DIAG source view: " + DisplayName(source.View));
            messages.Add(
                "END-DIAG source main bounds: X " + F(source.MainPartBounds.MinX) + " .. " + F(source.MainPartBounds.MaxX) +
                ", Y " + F(source.MainPartBounds.MinY) + " .. " + F(source.MainPartBounds.MaxY));
            messages.Add(
                "END-DIAG source frame: " + F(source.View.Width) + " x " + F(source.View.Height) +
                " paper mm; scale=" + F(source.View.Attributes == null ? 0.0 : source.View.Attributes.Scale));
            messages.Add("END-DIAG source DisplayCS: " + Cs(source.View.DisplayCoordinateSystem));
            messages.Add("END-DIAG source ViewCS:    " + Cs(source.View.ViewCoordinateSystem));

            var assemblyParts = GetAssemblyParts(analysis.MainPart);
            var allowedIds = new HashSet<int>(assemblyParts.Select(part => part.Identifier.ID));
            var ends = DetectEndPlatesVerbose(analysis.MainPart, assemblyParts, messages);
            var rescued = 0;

            if (ends.Start != null && !HasGeneratedSection(analysis.Drawing, "TDA_SECTION_A"))
            {
                if (ProbeOneEnd(analysis, source, ends.Start, true, allowedIds, messages) != null)
                    rescued++;
            }
            else if (ends.Start != null)
            {
                messages.Add("END-DIAG A: TDA_SECTION_A already exists; diagnostic creation skipped.");
            }

            if (ends.Finish != null && !HasGeneratedSection(analysis.Drawing, "TDA_SECTION_B"))
            {
                if (ProbeOneEnd(analysis, source, ends.Finish, false, allowedIds, messages) != null)
                    rescued++;
            }
            else if (ends.Finish != null)
            {
                messages.Add("END-DIAG B: TDA_SECTION_B already exists; diagnostic creation skipped.");
            }

            messages.Add("END-DIAG ==================================================");
            return rescued;
        }

        private EndDetection DetectEndPlatesVerbose(
            ModelPart mainPart,
            IList<ModelPart> assemblyParts,
            IList<string> messages)
        {
            var result = new EndDetection();
            var workPlaneHandler = _model.GetWorkPlaneHandler();
            var original = workPlaneHandler.GetCurrentTransformationPlane();

            try
            {
                workPlaneHandler.SetCurrentTransformationPlane(new TransformationPlane(mainPart.GetCoordinateSystem()));

                var mainSolid = mainPart.GetSolid();
                var mainMinX = mainSolid.MinimumPoint.X;
                var mainMaxX = mainSolid.MaximumPoint.X;
                var memberLength = Math.Abs(mainMaxX - mainMinX);
                var endZone = Math.Max(100.0, Math.Min(400.0, memberLength * 0.04));

                messages.Add(
                    "END-DIAG member-local main box: min=" + P(mainSolid.MinimumPoint) +
                    " max=" + P(mainSolid.MaximumPoint));
                messages.Add(
                    "END-DIAG member length=" + F(memberLength) +
                    "; end zone=" + F(endZone) +
                    "; assembly parts=" + assemblyParts.Count);
                messages.Add("END-DIAG plate candidate scan:");

                foreach (var part in assemblyParts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID)
                        continue;

                    if (!IsPlateLike(part))
                    {
                        messages.Add("  skip " + Describe(part) + " - not plate-like.");
                        continue;
                    }

                    var solid = part.GetSolid();
                    var minX = solid.MinimumPoint.X;
                    var maxX = solid.MaximumPoint.X;
                    var minY = solid.MinimumPoint.Y;
                    var maxY = solid.MaximumPoint.Y;
                    var minZ = solid.MinimumPoint.Z;
                    var maxZ = solid.MaximumPoint.Z;
                    var spanX = Math.Abs(maxX - minX);
                    var spanY = Math.Abs(maxY - minY);
                    var spanZ = Math.Abs(maxZ - minZ);
                    var transverseSpan = Math.Max(spanY, spanZ);
                    var centreX = (minX + maxX) * 0.5;
                    var distanceToStart = Math.Abs(centreX - mainMinX);
                    var distanceToFinish = Math.Abs(centreX - mainMaxX);
                    var tolerance = endZone + spanX * 0.5;
                    var transversePlate = spanX <= Math.Max(60.0, transverseSpan * 0.65);
                    var inStart = transversePlate && distanceToStart <= tolerance;
                    var inFinish = transversePlate && distanceToFinish <= tolerance;

                    messages.Add(
                        "  " + Describe(part) +
                        " box X[" + F(minX) + "," + F(maxX) + "]" +
                        " Y[" + F(minY) + "," + F(maxY) + "]" +
                        " Z[" + F(minZ) + "," + F(maxZ) + "]" +
                        " spans=" + F(spanX) + "/" + F(spanY) + "/" + F(spanZ) +
                        " dA=" + F(distanceToStart) +
                        " dB=" + F(distanceToFinish) +
                        " tol=" + F(tolerance) +
                        " transverse=" + transversePlate +
                        " => A=" + inStart + ", B=" + inFinish);

                    if (inStart && (result.Start == null || distanceToStart < result.Start.DistanceFromEnd))
                        result.Start = new EndPlateCandidate(part, distanceToStart);

                    if (inFinish && (result.Finish == null || distanceToFinish < result.Finish.DistanceFromEnd))
                        result.Finish = new EndPlateCandidate(part, distanceToFinish);
                }
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(original);
            }

            messages.Add(
                "END-DIAG selected A=" + (result.Start == null ? "<none>" : Describe(result.Start.Part)) +
                "; B=" + (result.Finish == null ? "<none>" : Describe(result.Finish.Part)));
            return result;
        }

        private DrawingView ProbeOneEnd(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            EndPlateCandidate candidate,
            bool startEnd,
            ISet<int> assemblyPartIds,
            IList<string> messages)
        {
            var letter = startEnd ? "A" : "B";
            var candidateBox = GetPartBox(candidate.Part, source.View.DisplayCoordinateSystem);
            var globalCentre = GetPartCentreGlobal(candidate.Part);
            var matrixCentre = MatrixFactory
                .ToCoordinateSystem(source.View.DisplayCoordinateSystem)
                .Transform(globalCentre);

            messages.Add("END-DIAG " + letter + ": target=" + Describe(candidate.Part));
            messages.Add(
                "END-DIAG " + letter + ": target in source DisplayCS box min=" + P(candidateBox.Min) +
                " max=" + P(candidateBox.Max) +
                " centre=" + P(candidateBox.Centre));
            messages.Add(
                "END-DIAG " + letter + ": target global AABB centre=" + P(globalCentre) +
                " -> matrix transformed source centre=" + P(matrixCentre) +
                "; delta from workplane centre=" + F(Distance(matrixCentre, candidateBox.Centre)));

            var horizontalMember = Math.Abs(source.MainPartBounds.Width) >= Math.Abs(source.MainPartBounds.Height);
            var cutCoordinates = horizontalMember
                ? BuildCutCoordinates(candidateBox.Min.X, candidateBox.Centre.X, candidateBox.Max.X)
                : BuildCutCoordinates(candidateBox.Min.Y, candidateBox.Centre.Y, candidateBox.Max.Y);

            var attempts = new List<SectionAttempt>();
            attempts.Add(new SectionAttempt(cutCoordinates[0], 300.0, "centre / depth 300"));
            attempts.Add(new SectionAttempt(cutCoordinates[0], 1500.0, "centre / depth 1500"));
            attempts.Add(new SectionAttempt(cutCoordinates[1], 1500.0, "near face 1 / depth 1500"));
            attempts.Add(new SectionAttempt(cutCoordinates[2], 1500.0, "near face 2 / depth 1500"));

            for (var i = 0; i < attempts.Count; i++)
            {
                var attempt = attempts[i];
                Point lineStart;
                Point lineEnd;
                BuildSectionLine(
                    source.MainPartBounds,
                    attempt.CutCoordinate,
                    horizontalMember,
                    startEnd,
                    out lineStart,
                    out lineEnd);

                var insertion = GetInitialInsertionPoint(source.View, startEnd);
                messages.Add(
                    "END-DIAG " + letter + " attempt " + (i + 1) + ": " + attempt.Description +
                    "; cut=" + F(attempt.CutCoordinate) +
                    "; line " + P(lineStart) + " -> " + P(lineEnd) +
                    "; insertion(sheet)=" + P(insertion));

                var attributes = CreateViewAttributes(source.View);
                var markAttributes = new SectionMarkBase.SectionMarkAttributes
                {
                    MarkName = letter
                };

                DrawingView sectionView;
                SectionMark sectionMark;
                var ok = DrawingView.CreateSectionView(
                    source.View,
                    lineStart,
                    lineEnd,
                    insertion,
                    attempt.Depth,
                    attempt.Depth,
                    attributes,
                    markAttributes,
                    out sectionView,
                    out sectionMark);

                messages.Add(
                    "END-DIAG " + letter + " attempt " + (i + 1) +
                    ": CreateSectionView returned " + ok +
                    "; viewNull=" + (sectionView == null) +
                    "; markNull=" + (sectionMark == null));

                if (!ok || sectionView == null)
                    continue;

                sectionView.Name = "TDA_DIAG_" + letter + "_" + (i + 1);
                sectionView.Modify();
                analysis.Drawing.CommitChanges();

                LogCreatedSection(sectionView, candidate.Part, assemblyPartIds, letter, i + 1, messages);

                if (ContainsPart(sectionView, candidate.Part.Identifier.ID))
                {
                    sectionView.Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B";
                    CleanSectionView(sectionView, assemblyPartIds);
                    PlaceEndSection(analysis.Drawing, source.View, sectionView, startEnd);
                    sectionView.Modify();
                    analysis.Drawing.CommitChanges();

                    messages.Add(
                        "END-DIAG " + letter + ": SUCCESS on attempt " + (i + 1) +
                        " (" + attempt.Description + "). Keeping this as the end section.");
                    return sectionView;
                }

                if (sectionMark != null)
                    sectionMark.Delete();
                sectionView.Delete();
                analysis.Drawing.CommitChanges();
                messages.Add("END-DIAG " + letter + " attempt " + (i + 1) + ": target plate absent; temporary section deleted.");
            }

            messages.Add("END-DIAG " + letter + ": all section attempts failed to contain the detected plate.");
            return null;
        }

        private void LogCreatedSection(
            DrawingView view,
            ModelPart target,
            ISet<int> assemblyPartIds,
            string letter,
            int attempt,
            IList<string> messages)
        {
            messages.Add(
                "END-DIAG " + letter + " attempt " + attempt +
                ": created frame=" + F(view.Width) + "x" + F(view.Height) +
                " origin=" + P(view.Origin) +
                " scale=" + F(view.Attributes == null ? 0.0 : view.Attributes.Scale));
            messages.Add("END-DIAG " + letter + " attempt " + attempt + ": DisplayCS=" + Cs(view.DisplayCoordinateSystem));
            messages.Add("END-DIAG " + letter + " attempt " + attempt + ": ViewCS=" + Cs(view.ViewCoordinateSystem));

            if (view.RestrictionBox != null)
            {
                messages.Add(
                    "END-DIAG " + letter + " attempt " + attempt +
                    ": RestrictionBox min=" + P(view.RestrictionBox.MinPoint) +
                    " max=" + P(view.RestrictionBox.MaxPoint));
            }

            var targetInSection = GetPartBox(target, view.DisplayCoordinateSystem);
            messages.Add(
                "END-DIAG " + letter + " attempt " + attempt +
                ": target box in CREATED section DisplayCS min=" + P(targetInSection.Min) +
                " max=" + P(targetInSection.Max) +
                " centre=" + P(targetInSection.Centre));

            var ids = new List<string>();
            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var drawingPart = parts.Current as DrawingPart;
                if (drawingPart == null || drawingPart.ModelIdentifier == null)
                    continue;

                var modelPart = _model.SelectModelObject(drawingPart.ModelIdentifier) as ModelPart;
                var text = drawingPart.ModelIdentifier.ID.ToString();
                if (modelPart != null)
                    text = Describe(modelPart);

                if (drawingPart.ModelIdentifier.ID == target.Identifier.ID)
                    text += " <TARGET>";
                else if (assemblyPartIds.Contains(drawingPart.ModelIdentifier.ID))
                    text += " <ASSEMBLY>";
                else
                    text += " <OTHER>";

                ids.Add(text);
            }

            messages.Add(
                "END-DIAG " + letter + " attempt " + attempt +
                ": drawing parts returned=" + ids.Count +
                (ids.Count == 0 ? " <none>" : " -> " + string.Join(", ", ids)));
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

        private Point GetPartCentreGlobal(ModelPart part)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane());
                var solid = part.GetSolid();
                return Mid(solid.MinimumPoint, solid.MaximumPoint);
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static double[] BuildCutCoordinates(double min, double centre, double max)
        {
            var span = Math.Abs(max - min);
            var nudge = Math.Min(2.0, Math.Max(0.2, span * 0.10));
            return new[]
            {
                centre,
                min + nudge,
                max - nudge
            };
        }

        private static void BuildSectionLine(
            ViewBounds mainBounds,
            double cutCoordinate,
            bool horizontalMember,
            bool startEnd,
            out Point start,
            out Point end)
        {
            if (horizontalMember)
            {
                var margin = Math.Max(40.0, Math.Abs(mainBounds.Height) * 0.25);
                start = new Point(cutCoordinate, mainBounds.MaxY + margin, 0.0);
                end = new Point(cutCoordinate, mainBounds.MinY - margin, 0.0);

                if (!startEnd)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }
            }
            else
            {
                var margin = Math.Max(40.0, Math.Abs(mainBounds.Width) * 0.25);
                start = new Point(mainBounds.MaxX + margin, cutCoordinate, 0.0);
                end = new Point(mainBounds.MinX - margin, cutCoordinate, 0.0);

                if (startEnd)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }
            }
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
                if (part == null || part.ModelIdentifier == null)
                    continue;

                if (assemblyPartIds.Contains(part.ModelIdentifier.ID))
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

        private static bool ContainsPart(DrawingView view, int modelId)
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

        private static void PlaceEndSection(Drawing drawing, DrawingView source, DrawingView view, bool startEnd)
        {
            var horizontal = source.Width * 0.5 + view.Width * 0.5 + ViewGap;
            var desired = new Point(
                source.Origin.X + (startEnd ? -horizontal : horizontal),
                source.Origin.Y,
                0.0);

            var sheet = drawing.GetSheet();
            if (sheet != null && sheet.Width > 0.0 && sheet.Height > 0.0)
            {
                var halfWidth = view.Width * 0.5 + ViewGap;
                var halfHeight = view.Height * 0.5 + ViewGap;
                desired.X = Math.Max(halfWidth, Math.Min(sheet.Width - halfWidth, desired.X));
                desired.Y = Math.Max(halfHeight, Math.Min(sheet.Height - halfHeight, desired.Y));
            }

            view.Origin = desired;
        }

        private static Point GetInitialInsertionPoint(DrawingView source, bool startEnd)
        {
            var horizontal = source.Width * 0.5 + 30.0;
            return new Point(
                source.Origin.X + (startEnd ? -horizontal : horizontal),
                source.Origin.Y,
                0.0);
        }

        private static bool HasGeneratedSection(Drawing drawing, string name)
        {
            if (drawing == null)
                return false;

            var views = drawing.GetSheet().GetAllViews();
            while (views.MoveNext())
            {
                var view = views.Current as DrawingView;
                if (view != null && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static ViewAnalysis GetBaseView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                .Where(view => !IsGenerated(view.View))
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
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

            var secondaries = assembly.GetSecondaries();
            foreach (var item in secondaries)
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

        private static string DisplayName(DrawingView view)
        {
            if (view == null || string.IsNullOrWhiteSpace(view.Name))
                return "unnamed original view";
            return view.Name;
        }

        private static string F(double value)
        {
            return value.ToString("0.###");
        }

        private static string P(Point point)
        {
            if (point == null)
                return "<null>";
            return "(" + F(point.X) + ", " + F(point.Y) + ", " + F(point.Z) + ")";
        }

        private static string V(Vector vector)
        {
            if (vector == null)
                return "<null>";
            return "(" + F(vector.X) + ", " + F(vector.Y) + ", " + F(vector.Z) + ")";
        }

        private static string Cs(CoordinateSystem cs)
        {
            if (cs == null)
                return "<null>";
            return "O=" + P(cs.Origin) + " X=" + V(cs.AxisX) + " Y=" + V(cs.AxisY) +
                   " N=" + V(Cross(cs.AxisX, cs.AxisY));
        }

        private static Vector Cross(Vector a, Vector b)
        {
            return new Vector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static double Distance(Point a, Point b)
        {
            if (a == null || b == null)
                return double.NaN;
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Point Mid(Point a, Point b)
        {
            return new Point(
                (a.X + b.X) * 0.5,
                (a.Y + b.Y) * 0.5,
                (a.Z + b.Z) * 0.5);
        }

        private sealed class PartBox
        {
            public PartBox(Point min, Point max)
            {
                Min = min;
                Max = max;
                Centre = Mid(min, max);
            }

            public Point Min { get; }
            public Point Max { get; }
            public Point Centre { get; }
        }

        private sealed class EndDetection
        {
            public EndPlateCandidate Start { get; set; }
            public EndPlateCandidate Finish { get; set; }
        }

        private sealed class EndPlateCandidate
        {
            public EndPlateCandidate(ModelPart part, double distanceFromEnd)
            {
                Part = part;
                DistanceFromEnd = distanceFromEnd;
            }

            public ModelPart Part { get; }
            public double DistanceFromEnd { get; }
        }

        private sealed class SectionAttempt
        {
            public SectionAttempt(double cutCoordinate, double depth, string description)
            {
                CutCoordinate = cutCoordinate;
                Depth = depth;
                Description = description;
            }

            public double CutCoordinate { get; }
            public double Depth { get; }
            public string Description { get; }
        }
    }
}
