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
    /// End views are not decided from a secondary part's own local axes.
    /// They are decided from where plate-like secondary parts physically sit along
    /// the main member's local X axis. This is much more reliable for end plates.
    /// </summary>
    public sealed class BeamEndViewEnsurer
    {
        private const double ViewGap = 12.0;

        private readonly Model _model;

        public BeamEndViewEnsurer(Model model)
        {
            _model = model;
        }

        public int EnsureEndViews(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var source = GetBaseView(analysis);
            if (source == null || source.MainPartBounds == null)
            {
                messages?.Add("End detection: no usable base view was found.");
                return 0;
            }

            var detection = DetectEndPlates(analysis.MainPart, messages);
            if (detection.Start == null && detection.Finish == null)
            {
                messages?.Add("End detection: no transverse plate-like secondary was found at either member end.");
                return 0;
            }

            var assemblyPartIds = new HashSet<int>(
                GetAssemblyParts(analysis.MainPart).Select(part => part.Identifier.ID));

            var created = 0;

            if (detection.Start != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_A");
                if (CreateEndSection(
                    analysis,
                    source,
                    detection.Start,
                    true,
                    assemblyPartIds,
                    messages) != null)
                {
                    created++;
                }
            }

            if (detection.Finish != null)
            {
                DeleteGeneratedSection(analysis.Drawing, "TDA_SECTION_B");
                if (CreateEndSection(
                    analysis,
                    source,
                    detection.Finish,
                    false,
                    assemblyPartIds,
                    messages) != null)
                {
                    created++;
                }
            }

            analysis.Drawing.CommitChanges();
            return created;
        }

        private EndDetection DetectEndPlates(ModelPart mainPart, IList<string> messages)
        {
            var result = new EndDetection();
            var originalPlane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();

            try
            {
                // Everything is measured in the main part's own coordinate system.
                // X is therefore the member longitudinal axis regardless of the global model orientation.
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(
                    new TransformationPlane(mainPart.GetCoordinateSystem()));

                var mainSolid = mainPart.GetSolid();
                var mainMinX = mainSolid.MinimumPoint.X;
                var mainMaxX = mainSolid.MaximumPoint.X;
                var memberLength = Math.Abs(mainMaxX - mainMinX);
                var endZone = Math.Max(100.0, Math.Min(400.0, memberLength * 0.04));

                messages?.Add(
                    "End detection: main-part local X runs " +
                    mainMinX.ToString("0.0") + " to " + mainMaxX.ToString("0.0") +
                    "; end search zone = " + endZone.ToString("0") + " mm.");

                foreach (var part in GetAssemblyParts(mainPart))
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID || !IsPlateLike(part))
                        continue;

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

                    // An end plate has its large face across the member end, so its extent
                    // along the member is small compared with its face width/depth.
                    var transversePlate = spanX <= Math.Max(60.0, transverseSpan * 0.65);
                    if (!transversePlate)
                        continue;

                    var distanceToStart = Math.Abs(centreX - mainMinX);
                    var distanceToFinish = Math.Abs(centreX - mainMaxX);
                    var tolerance = endZone + spanX * 0.5;

                    if (distanceToStart <= tolerance &&
                        (result.Start == null || distanceToStart < result.Start.DistanceFromEnd))
                    {
                        result.Start = new EndPlateCandidate(part, distanceToStart);
                    }

                    if (distanceToFinish <= tolerance &&
                        (result.Finish == null || distanceToFinish < result.Finish.DistanceFromEnd))
                    {
                        result.Finish = new EndPlateCandidate(part, distanceToFinish);
                    }
                }

                if (result.Start != null)
                    messages?.Add("End A plate detected: " + Describe(result.Start.Part) + ".");

                if (result.Finish != null)
                    messages?.Add("End B plate detected: " + Describe(result.Finish.Part) + ".");
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(originalPlane);
            }

            return result;
        }

        private DrawingView CreateEndSection(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            EndPlateCandidate candidate,
            bool startEnd,
            ISet<int> assemblyPartIds,
            IList<string> messages)
        {
            var originalPlane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
            Point plateCentreInView;

            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(
                    new TransformationPlane(source.View.DisplayCoordinateSystem));

                var solid = candidate.Part.GetSolid();
                plateCentreInView = new Point(
                    (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5,
                    (solid.MinimumPoint.Y + solid.MaximumPoint.Y) * 0.5,
                    (solid.MinimumPoint.Z + solid.MaximumPoint.Z) * 0.5);
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(originalPlane);
            }

            Point lineStart;
            Point lineEnd;
            GetSectionLineThroughFeature(
                source.MainPartBounds,
                plateCentreInView,
                startEnd,
                out lineStart,
                out lineEnd);

            var attributes = CreateViewAttributes(source.View);
            var markAttributes = new SectionMarkBase.SectionMarkAttributes
            {
                MarkName = startEnd ? "A" : "B"
            };

            var insertionPoint = GetInitialInsertionPoint(source.View, startEnd);
            DrawingView sectionView;
            SectionMark sectionMark;

            var ok = DrawingView.CreateSectionView(
                source.View,
                lineStart,
                lineEnd,
                insertionPoint,
                300.0,
                300.0,
                attributes,
                markAttributes,
                out sectionView,
                out sectionMark);

            if (!ok || sectionView == null)
            {
                messages?.Add(
                    "Could not create end section " + (startEnd ? "A-A" : "B-B") +
                    " through " + Describe(candidate.Part) + ".");
                return null;
            }

            sectionView.Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B";
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            CleanSectionView(sectionView, assemblyPartIds);
            analysis.Drawing.CommitChanges();

            if (!ContainsPart(sectionView, candidate.Part.Identifier.ID))
            {
                if (sectionMark != null)
                    sectionMark.Delete();

                sectionView.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add(
                    "Rejected end section " + (startEnd ? "A-A" : "B-B") +
                    " because the detected end plate was not visible in the created section.");
                return null;
            }

            PlaceEndSection(analysis.Drawing, source.View, sectionView, startEnd);
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add(
                "Created end section " + (startEnd ? "A-A" : "B-B") +
                " through detected plate " + Describe(candidate.Part) + ".");

            return sectionView;
        }

        private static void GetSectionLineThroughFeature(
            ViewBounds mainBounds,
            Point featureCentre,
            bool startEnd,
            out Point start,
            out Point end)
        {
            if (Math.Abs(mainBounds.Width) >= Math.Abs(mainBounds.Height))
            {
                var x = featureCentre.X;
                var margin = Math.Max(40.0, Math.Abs(mainBounds.Height) * 0.25);
                start = new Point(x, mainBounds.MinY - margin, 0.0);
                end = new Point(x, mainBounds.MaxY + margin, 0.0);

                if (!startEnd)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }
            }
            else
            {
                var y = featureCentre.Y;
                var margin = Math.Max(40.0, Math.Abs(mainBounds.Width) * 0.25);
                start = new Point(mainBounds.MinX - margin, y, 0.0);
                end = new Point(mainBounds.MaxX + margin, y, 0.0);

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
    }
}
