using NVSPlotter.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;


namespace NVSPlotter.Services
{
    /// <summary>
    /// Handles all coordinate transformations between different coordinate systems:
    /// - Viewport space (ScrollViewer coordinates)
    /// - Canvas space (DrawCanvas coordinates, 1px = 1mm before zoom)
    /// - Document space (millimeters, clamped to page bounds)
    /// - Bed space (machine bed coordinates with margins and rotation)
    /// - Work space (GRBL work coordinates relative to home position)
    /// </summary>
    public sealed class CoordinateTransformService
    {
        private readonly Canvas _drawCanvas;
        private readonly ScrollViewer _canvasScroll;
        private readonly Func<PlotDocument> _getDocument;
        private readonly Func<double> _getBedX;
        private readonly Func<double> _getBedY;
        private readonly Func<bool> _getHomeAtMaxX;
        private readonly Func<bool> _getHomeAtMaxY;
        private readonly Func<double> _getSafeMargin;

        /// <summary>
        /// Initializes the coordinate transform service.
        /// </summary>
        /// <param name="drawCanvas">The main drawing canvas</param>
        /// <param name="canvasScroll">The scroll viewer containing the canvas</param>
        /// <param name="getDocument">Function to get the current document</param>
        /// <param name="getBedX">Function to get bed X size (mm)</param>
        /// <param name="getBedY">Function to get bed Y size (mm)</param>
        /// <param name="getHomeAtMaxX">Function to check if homing is at max X</param>
        /// <param name="getHomeAtMaxY">Function to check if homing is at max Y</param>
        /// <param name="getSafeMargin">Function to get safe margin (mm)</param>
        public CoordinateTransformService(
            Canvas drawCanvas,
            ScrollViewer canvasScroll,
            Func<PlotDocument> getDocument,
            Func<double> getBedX,
            Func<double> getBedY,
            Func<bool> getHomeAtMaxX,
            Func<bool> getHomeAtMaxY,
            Func<double> getSafeMargin)
        {
            _drawCanvas = drawCanvas ?? throw new ArgumentNullException(nameof(drawCanvas));
            _canvasScroll = canvasScroll ?? throw new ArgumentNullException(nameof(canvasScroll));
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _getBedX = getBedX ?? throw new ArgumentNullException(nameof(getBedX));
            _getBedY = getBedY ?? throw new ArgumentNullException(nameof(getBedY));
            _getHomeAtMaxX = getHomeAtMaxX ?? throw new ArgumentNullException(nameof(getHomeAtMaxX));
            _getHomeAtMaxY = getHomeAtMaxY ?? throw new ArgumentNullException(nameof(getHomeAtMaxY));
            _getSafeMargin = getSafeMargin ?? throw new ArgumentNullException(nameof(getSafeMargin));
        }

        #region Viewport to Canvas Conversions

        /// <summary>
        /// Converts a point from viewport space (ScrollViewer) to canvas space (millimeters).
        /// This accounts for zoom and scroll position.
        /// </summary>
        /// <param name="viewportPoint">Point in viewport coordinates</param>
        /// <returns>Point in canvas coordinates (mm)</returns>
        public PointMm MouseToMm(Point viewportPoint)
        {
            var canvasPoint = _canvasScroll.TranslatePoint(viewportPoint, _drawCanvas);
            return new PointMm(canvasPoint.X, canvasPoint.Y);
        }

        #endregion

        #region Document Space Operations

        /// <summary>
        /// Clamps a point to the document page boundaries.
        /// </summary>
        /// <param name="point">Point in document coordinates</param>
        /// <returns>Clamped point within [0, docWidth] x [0, docHeight]</returns>
        public PointMm ClampToPage(PointMm point)
        {
            var doc = _getDocument();
            var x = Math.Clamp(point.X, 0, doc.WidthMm);
            var y = Math.Clamp(point.Y, 0, doc.HeightMm);
            return new PointMm(x, y);
        }

        #endregion

        #region Bed Space Transformations

        /// <summary>
        /// Fit mode for document-to-bed transformation.
        /// </summary>
        public enum FitMode
        {
            /// <summary>No rotation - document orientation preserved</summary>
            None,
            /// <summary>Rotate 90° clockwise to better fit the bed</summary>
            RotateCW
        }

        /// <summary>
        /// Specifies how a document is fitted onto the machine bed.
        /// </summary>
        public readonly record struct FitSpec(
            FitMode Mode,
            double Scale,
            double Margin,
            double DocW,
            double DocH);

        /// <summary>
        /// Computes the optimal fit specification for placing a document on the bed.
        /// Considers both no-rotation and 90° CW rotation, choosing the one that
        /// maximizes scale while keeping it ≤ 1.0 (never scale up).
        /// </summary>
        /// <param name="docW">Document width (mm)</param>
        /// <param name="docH">Document height (mm)</param>
        /// <param name="bedX">Bed X size (mm)</param>
        /// <param name="bedY">Bed Y size (mm)</param>
        /// <param name="margin">Safe margin (mm) to leave around edges</param>
        /// <returns>Fit specification with mode, scale, and margins</returns>
        public FitSpec ComputeFit(double docW, double docH, double bedX, double bedY, double margin)
        {
            // Usable bed area after removing margins
            var usableX = Math.Max(1.0, bedX - margin * 2);
            var usableY = Math.Max(1.0, bedY - margin * 2);

            // No-rotate scale: fit doc as-is
            var scaleNone = Math.Min(usableX / docW, usableY / docH);

            // Rotate CW scale: swap doc dimensions
            var scaleRotate = Math.Min(usableX / docH, usableY / docW);

            // Never scale up above 1.0
            scaleNone = Math.Min(scaleNone, 1.0);
            scaleRotate = Math.Min(scaleRotate, 1.0);

            // Choose the mode that gives better (larger) scale
            if (scaleRotate > scaleNone)
            {
                return new FitSpec(FitMode.RotateCW, scaleRotate, margin, docW, docH);
            }

            return new FitSpec(FitMode.None, scaleNone, margin, docW, docH);
        }

        /// <summary>
        /// Transforms a document point to bed coordinates with clamping to safe margins.
        /// </summary>
        /// <param name="point">Point in document coordinates</param>
        /// <param name="fit">Fit specification</param>
        /// <returns>Point in bed coordinates</returns>
        public PointMm DocToBed(PointMm point, FitSpec fit)
        {
            return DocToBed(point, fit, clamp: true);
        }

        /// <summary>
        /// Transforms a document point to bed coordinates.
        /// </summary>
        /// <param name="point">Point in document coordinates</param>
        /// <param name="fit">Fit specification</param>
        /// <param name="clamp">If true, clamp to safe margins; if false, only clamp to physical bed limits</param>
        /// <returns>Point in bed coordinates</returns>
        public PointMm DocToBed(PointMm point, FitSpec fit, bool clamp)
        {
            var margin = fit.Margin;
            var scale = fit.Scale;

            double x, y;

            switch (fit.Mode)
            {
                case FitMode.RotateCW:
                    // Clockwise rotation: x' = y, y' = docW - x
                    x = margin + point.Y * scale;
                    y = margin + (fit.DocW - point.X) * scale;
                    break;

                default: // FitMode.None
                    x = margin + point.X * scale;
                    y = margin + point.Y * scale;
                    break;
            }

            if (clamp)
            {
                // Clamp to safe margins
                x = ClampBedX(x);
                y = ClampBedY(y);
            }
            else
            {
                // Only clamp to physical bed limits (0 to bed size)
                var bedX = _getBedX();
                var bedY = _getBedY();
                x = Math.Clamp(x, 0, bedX);
                y = Math.Clamp(y, 0, bedY);
            }

            return new PointMm(x, y);
        }

        /// <summary>
        /// Clamps X coordinate to safe bed area (respecting margins).
        /// </summary>
        /// <param name="x">X coordinate in bed space</param>
        /// <returns>Clamped X coordinate</returns>
        public double ClampBedX(double x)
        {
            var bedX = _getBedX();
            var margin = _getSafeMargin();
            return Math.Clamp(x, margin, bedX - margin);
        }

        /// <summary>
        /// Clamps Y coordinate to safe bed area (respecting margins).
        /// </summary>
        /// <param name="y">Y coordinate in bed space</param>
        /// <returns>Clamped Y coordinate</returns>
        public double ClampBedY(double y)
        {
            var bedY = _getBedY();
            var margin = _getSafeMargin();
            return Math.Clamp(y, margin, bedY - margin);
        }

        #endregion

        #region Work Space Transformations

        /// <summary>
        /// Converts bed-local coordinates to GRBL work coordinates relative to home (0,0).
        /// Enforces the user's required convention: X always positive, Y always negative.
        /// 
        /// The transformation accounts for the homing direction ($23 setting):
        /// - If home is at MAX end, distance = (max - position)
        /// - If home is at MIN end, distance = position
        /// 
        /// Then applies sign convention: X = +distance, Y = -distance
        /// </summary>
        /// <param name="bedPoint">Point in bed coordinates (origin at bed min corner)</param>
        /// <returns>Point in work coordinates (origin at home position)</returns>
        public PointMm BedToWork(PointMm bedPoint)
        {
            var bedX = _getBedX();
            var bedY = _getBedY();
            var homeAtMaxX = _getHomeAtMaxX();
            var homeAtMaxY = _getHomeAtMaxY();

            // Calculate distance from home into the bed (always positive)
            var distX = homeAtMaxX ? (bedX - bedPoint.X) : bedPoint.X;
            var distY = homeAtMaxY ? (bedY - bedPoint.Y) : bedPoint.Y;

            // Apply sign convention: X positive, Y negative
            var workX = Math.Max(0, distX);  // ALWAYS >= 0
            var workY = -Math.Max(0, distY); // ALWAYS <= 0

            return new PointMm(workX, workY);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the current document from the provider function.
        /// </summary>
        /// <returns>Current PlotDocument</returns>
        public PlotDocument GetDocument() => _getDocument();

        /// <summary>
        /// Gets the current bed X size.
        /// </summary>
        /// <returns>Bed X size in mm</returns>
        public double GetBedX() => _getBedX();

        /// <summary>
        /// Gets the current bed Y size.
        /// </summary>
        /// <returns>Bed Y size in mm</returns>
        public double GetBedY() => _getBedY();

        /// <summary>
        /// Gets the current safe margin.
        /// </summary>
        /// <returns>Safe margin in mm</returns>
        public double GetSafeMargin() => _getSafeMargin();

        /// <summary>
        /// Checks if homing is at max X position.
        /// </summary>
        /// <returns>True if home at max X</returns>
        public bool IsHomeAtMaxX() => _getHomeAtMaxX();

        /// <summary>
        /// Checks if homing is at max Y position.
        /// </summary>
        /// <returns>True if home at max Y</returns>
        public bool IsHomeAtMaxY() => _getHomeAtMaxY();

        #endregion
    }
}