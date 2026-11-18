using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Crop-region helpers: rectangular extents and non-rectangular sketches.
/// View.CropBox is ignored while a sketch is set, so the two are applied in order.
/// </summary>
internal static partial class AgentViewWriteHelper
{
    internal static ViewCropState ReadCropState(View view)
    {
        var state = new ViewCropState();
        try
        {
            state.Active = view.CropBoxActive;
            state.Visible = view.CropBoxVisible;
        }
        catch (Exception)
        {
            return state;
        }

        TryReadCropShape(view, state);

        var box = view.CropBox;
        if (box is null)
            return state;

        state.MinMm = new[]
        {
            AgentUnitsFormat.ToMillimeters(box.Min.X),
            AgentUnitsFormat.ToMillimeters(box.Min.Y),
            AgentUnitsFormat.ToMillimeters(box.Min.Z)
        };
        state.MaxMm = new[]
        {
            AgentUnitsFormat.ToMillimeters(box.Max.X),
            AgentUnitsFormat.ToMillimeters(box.Max.Y),
            AgentUnitsFormat.ToMillimeters(box.Max.Z)
        };
        return state;
    }

    private static void TryReadCropShape(View view, ViewCropState state)
    {
        try
        {
            var manager = view.GetCropRegionShapeManager();
            if (manager is null || !manager.ShapeSet)
                return;

            state.ShapeSet = true;
            var loops = manager.GetCropShape();
            if (loops.Count != 1)
                return;

            var points = new List<double[]>();
            foreach (var curve in loops[0])
            {
                if (curve is not Line line)
                    return;

                var start = line.GetEndPoint(0);
                points.Add(new[]
                {
                    AgentUnitsFormat.ToMillimeters(start.X),
                    AgentUnitsFormat.ToMillimeters(start.Y),
                    AgentUnitsFormat.ToMillimeters(start.Z)
                });
            }

            if (points.Count >= 3)
                state.ShapeMm = points;
        }
        catch (Exception)
        {
            // Templates and views without crop support reject the shape manager.
        }
    }

    internal static void ApplyCropState(View view, ViewCropState state, List<string> warnings)
    {
        SetCropActive(view, true, warnings);
        view.CropBoxVisible = state.Visible;

        // View.CropBox is ignored while a crop sketch is set, so the rectangle must be
        // removed before the extents are written (undo of a shape → rectangle).
        if (!state.ShapeSet)
            RemoveCropShape(view, warnings);

        if (state.MinMm is not null && state.MaxMm is not null
            && state.MinMm.Length >= 2 && state.MaxMm.Length >= 2)
        {
            var existing = view.CropBox;
            var minZ = existing?.Min.Z ?? 0.0;
            var maxZ = existing?.Max.Z ?? 0.0;
            if (state.MinMm.Length >= 3)
                minZ = ToInternal(state.MinMm[2]);
            if (state.MaxMm.Length >= 3)
                maxZ = ToInternal(state.MaxMm[2]);

            try
            {
                view.CropBox = new BoundingBoxXYZ
                {
                    Min = new XYZ(ToInternal(state.MinMm[0]), ToInternal(state.MinMm[1]), minZ),
                    Max = new XYZ(ToInternal(state.MaxMm[0]), ToInternal(state.MaxMm[1]), maxZ)
                };
            }
            catch (Exception ex)
            {
                warnings.Add("Crop extents could not be set on '" + view.Name + "': " + ex.Message);
            }
        }

        if (state.ShapeSet)
        {
            if (state.ShapeMm is { Count: >= 3 })
                ApplyCropShape(view, state.ShapeMm, warnings);
            else
                warnings.Add("The recorded crop shape of '" + view.Name +
                             "' is not restorable (only straight segments are supported); " +
                             "the crop extents were restored.");
        }

        if (!state.Active)
            SetCropActive(view, false, warnings);
    }

    private static void ApplyCropShape(
        View view,
        IReadOnlyList<double[]> points,
        List<string> warnings)
    {
        try
        {
            var manager = view.GetCropRegionShapeManager();
            if (manager is null || !manager.CanHaveShape)
            {
                warnings.Add("View '" + view.Name +
                             "' does not support a non-rectangular crop; the shape was not applied.");
                return;
            }

            var loop = new CurveLoop();
            for (var index = 0; index < points.Count; index++)
            {
                var start = ToXyz(points[index]);
                var end = ToXyz(points[(index + 1) % points.Count]);
                if (start.DistanceTo(end) < 1e-9)
                    continue;

                loop.Append(Line.CreateBound(start, end));
            }

            if (!manager.IsCropRegionShapeValid(loop))
            {
                warnings.Add("The crop shape for '" + view.Name +
                             "' is not a valid closed loop in the view plane; it was not applied.");
                return;
            }

            // SetCropShape requires an active and visible crop region; restore the
            // requested visibility afterwards (even when Revit rejects the shape).
            var restoreVisible = !view.CropBoxVisible;
            try
            {
                view.CropBoxActive = true;
                view.CropBoxVisible = true;
                manager.SetCropShape(loop);
            }
            finally
            {
                if (restoreVisible)
                    view.CropBoxVisible = false;
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Crop shape could not be applied to '" + view.Name + "': " + ex.Message);
        }
    }

    private static void RemoveCropShape(View view, List<string> warnings)
    {
        try
        {
            var manager = view.GetCropRegionShapeManager();
            if (manager is not null && manager.ShapeSet)
                manager.RemoveCropRegionShape();
        }
        catch (Exception ex)
        {
            warnings.Add("Crop shape could not be removed from '" + view.Name + "': " + ex.Message);
        }
    }

    private static XYZ ToXyz(double[] millimetres) => new(
        ToInternal(millimetres[0]),
        ToInternal(millimetres[1]),
        millimetres.Length > 2 ? ToInternal(millimetres[2]) : 0.0);

    /// <summary>
    /// Copies the crop region of <paramref name="source"/> to <paramref name="target"/>.
    /// Crop shape coordinates are in the view frame, so the copy works across
    /// views of the same orientation (mvp-plan §7A.6).
    /// </summary>
    internal static void CopyCrop(View source, View target, List<string> warnings)
    {
        var sourceBox = ReadCropState(source);
        var targetBox = ReadCropState(target);

        target.CropBoxActive = true;
        target.CropBoxVisible = sourceBox.Visible;

        if (!sourceBox.ShapeSet && targetBox.ShapeSet)
            RemoveCropShape(target, warnings);

        if (sourceBox.MinMm is not null && sourceBox.MaxMm is not null)
        {
            target.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(
                    ToInternal(sourceBox.MinMm[0]),
                    ToInternal(sourceBox.MinMm[1]),
                    ToInternal(targetBox.MinMm is { Length: >= 3 } ? targetBox.MinMm[2] : 0.0)),
                Max = new XYZ(
                    ToInternal(sourceBox.MaxMm[0]),
                    ToInternal(sourceBox.MaxMm[1]),
                    ToInternal(targetBox.MaxMm is { Length: >= 3 } ? targetBox.MaxMm[2] : 0.0))
            };
        }

        if (sourceBox.ShapeSet)
        {
            if (sourceBox.ShapeMm is { Count: >= 3 })
            {
                ApplyCropShape(target, sourceBox.ShapeMm, warnings);
            }
            else
            {
                warnings.Add("Non-rectangular crop of '" + source.Name +
                             "' could not be read as straight segments; only the extents were copied.");
            }
        }

        if (!sourceBox.Active)
            SetCropActive(target, false, warnings);
    }

    private static void SetCropActive(View view, bool active, List<string> warnings)
    {
        try
        {
            view.CropBoxActive = active;
        }
        catch (Exception ex)
        {
            warnings.Add("CropBoxActive could not be set for '" + view.Name + "': " + ex.Message);
        }
    }

}
