using NVSPlotter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Handles keyboard shortcuts and clipboard operations for the application.
    /// Centralizes keyboard input handling to reduce complexity in MainWindow.
    /// </summary>
    public sealed class KeyboardShortcutHandler
    {
        private readonly Func<PlotDocument> _getDocument;
        private readonly Action<PlotDocument> _setDocument;
        private readonly SelectionController _selectionController;
        private readonly ClipboardService _clipboardService;
        private readonly ShapeDrawingController _shapeController;
        private readonly MeasurementOverlay _measurementOverlay;
        private readonly PaintWellController _paintWellController;
        private readonly Action _renderAll;
        private readonly Action<string> _log;
        private readonly Func<ToolMode> _getCurrentTool;
        private readonly Action<ToolMode> _setCurrentTool;
        private readonly Stack<int> _undoGroupSizes;
        private readonly Action _invalidateGcode;
        private readonly Action _updatePaintWellsUI;

        // Subdivision state callbacks
        private readonly Func<bool> _isSubdividing;
        private readonly Action _cancelSubdivision;

        public KeyboardShortcutHandler(
            Func<PlotDocument> getDocument,
            Action<PlotDocument> setDocument,
            SelectionController selectionController,
            ClipboardService clipboardService,
            ShapeDrawingController shapeController,
            MeasurementOverlay measurementOverlay,
            PaintWellController paintWellController,
            Action renderAll,
            Action<string> log,
            Func<ToolMode> getCurrentTool,
            Action<ToolMode> setCurrentTool,
            Stack<int> undoGroupSizes,
            Action invalidateGcode,
            Action updatePaintWellsUI,
            Func<bool> isSubdividing,
            Action cancelSubdivision)
        {
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _setDocument = setDocument ?? throw new ArgumentNullException(nameof(setDocument));
            _selectionController = selectionController ?? throw new ArgumentNullException(nameof(selectionController));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _shapeController = shapeController ?? throw new ArgumentNullException(nameof(shapeController));
            _measurementOverlay = measurementOverlay ?? throw new ArgumentNullException(nameof(measurementOverlay));
            _paintWellController = paintWellController ?? throw new ArgumentNullException(nameof(paintWellController));
            _renderAll = renderAll ?? throw new ArgumentNullException(nameof(renderAll));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _getCurrentTool = getCurrentTool ?? throw new ArgumentNullException(nameof(getCurrentTool));
            _setCurrentTool = setCurrentTool ?? throw new ArgumentNullException(nameof(setCurrentTool));
            _undoGroupSizes = undoGroupSizes ?? throw new ArgumentNullException(nameof(undoGroupSizes));
            _invalidateGcode = invalidateGcode ?? throw new ArgumentNullException(nameof(invalidateGcode));
            _updatePaintWellsUI = updatePaintWellsUI ?? throw new ArgumentNullException(nameof(updatePaintWellsUI));
            _isSubdividing = isSubdividing ?? throw new ArgumentNullException(nameof(isSubdividing));
            _cancelSubdivision = cancelSubdivision ?? throw new ArgumentNullException(nameof(cancelSubdivision));
        }

        /// <summary>
        /// Handles keyboard shortcuts. Returns true if the key was handled.
        /// </summary>
        /// <param name="key">The key that was pressed</param>
        /// <param name="modifiers">Current modifier keys</param>
        /// <param name="isTextBoxFocused">Whether a TextBox has focus</param>
        /// <returns>True if the key was handled, false otherwise</returns>
        public bool HandleKeyDown(Key key, ModifierKeys modifiers, bool isTextBoxFocused)
        {
            // Subdivision mode - Escape to cancel
            if (_isSubdividing() && key == Key.Escape)
            {
                _cancelSubdivision();
                return true;
            }

            // Skip clipboard shortcuts when in a text box
            if (!isTextBoxFocused && (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.X:
                        PerformCut();
                        return true;
                    case Key.C:
                        PerformCopy();
                        return true;
                    case Key.V:
                        PerformPaste();
                        return true;
                    case Key.A:
                        PerformSelectAll();
                        return true;
                    case Key.Z:
                        PerformUndo();
                        return true;
                    case Key.G:
                        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            // Ctrl+Shift+G = Ungroup (handled externally)
                            return false;
                        }
                        // Ctrl+G = Group (handled externally)
                        return false;
                }
            }

            // Selection tool shortcuts
            if (_getCurrentTool() == ToolMode.Select)
            {
                if (key == Key.Escape)
                {
                    if (_selectionController.IsActive)
                    {
                        _selectionController.Cancel();
                    }
                    else if (_selectionController.HasSelection)
                    {
                        _selectionController.ClearSelection();
                    }
                    return true;
                }

                if (key == Key.Delete && _selectionController.HasSelection)
                {
                    _selectionController.DeleteSelection();
                    _invalidateGcode();
                    return true;
                }
            }

            // Measurement tool - Escape to cancel
            if (_measurementOverlay.IsMeasuring && key == Key.Escape)
            {
                _measurementOverlay.Reset();
                return true;
            }

            // Bezier tool - Escape to cancel, Enter to finish
            if (_shapeController.IsBezierActive && (key == Key.Escape || key == Key.Enter || key == Key.Return))
            {
                if (key == Key.Escape)
                {
                    _shapeController.CancelBezier();
                }
                else
                {
                    _shapeController.TryFinishBezier();
                }
                return true;
            }

            // PolyBezier tool - Escape to cancel, Enter to finish
            if (_shapeController.IsPolyBezierActive && (key == Key.Escape || key == Key.Enter || key == Key.Return))
            {
                if (key == Key.Escape)
                {
                    _shapeController.CancelPolyBezier();
                }
                else
                {
                    _shapeController.TryFinishPolyBezier();
                }
                return true;
            }

            // Polyline tool - Escape to cancel, Enter to finish
            if (_shapeController.IsPolylineActive && (key == Key.Escape || key == Key.Enter || key == Key.Return))
            {
                _shapeController.FinishPolyline();
                return true;
            }

            return false;
        }

        #region Clipboard Operations

        /// <summary>
        /// Copies selected strokes and paint wells to the clipboard.
        /// </summary>
        public void PerformCopy()
        {
            if (!_selectionController.HasSelection)
            {
                _log("Nothing selected to copy.");
                return;
            }

            var strokes = _selectionController.GetSelectedStrokes();
            var wells = _selectionController.GetSelectedPaintWells();
            var bounds = _selectionController.SelectionBounds;

            _clipboardService.CopyToClipboard(strokes, wells, bounds);
            _log($"Copied {strokes.Count} stroke(s) and {wells.Count} paint well(s) to clipboard.");
        }

        /// <summary>
        /// Cuts selected strokes and paint wells to the clipboard.
        /// </summary>
        public void PerformCut()
        {
            if (!_selectionController.HasSelection)
            {
                _log("Nothing selected to cut.");
                return;
            }

            // First copy
            var strokes = _selectionController.GetSelectedStrokes();
            var wells = _selectionController.GetSelectedPaintWells();
            var bounds = _selectionController.SelectionBounds;
            _clipboardService.CopyToClipboard(strokes, wells, bounds);

            // Then delete
            _selectionController.DeleteSelection();
            _invalidateGcode();

            _log($"Cut {strokes.Count} stroke(s) and {wells.Count} paint well(s) to clipboard.");
        }

        /// <summary>
        /// Pastes strokes and paint wells from the clipboard.
        /// </summary>
        public void PerformPaste()
        {
            if (!_clipboardService.HasClipboardData())
            {
                _log("Clipboard is empty or contains incompatible data.");
                return;
            }

            var result = _clipboardService.PasteFromClipboard();
            if (result == null)
            {
                _log("Failed to paste from clipboard.");
                return;
            }

            var (strokes, wells) = result.Value;
            var doc = _getDocument();

            // Add strokes to document
            var startIndex = doc.Strokes.Count;
            foreach (var stroke in strokes)
            {
                doc.Strokes.Add(stroke);
            }

            // Add paint wells to document
            foreach (var well in wells)
            {
                doc.PaintWells.Add(well);
            }

            // Select the pasted items
            _selectionController.ClearSelection();

            // Select strokes
            var indices = Enumerable.Range(startIndex, strokes.Count).ToList();
            _selectionController.SelectStrokes(indices);

            // Select paint wells
            _selectionController.SelectPaintWells(wells.Select(w => w.Id));

            // Switch to Select tool to show the pasted items
            _setCurrentTool(ToolMode.Select);

            _invalidateGcode();
            _updatePaintWellsUI();
            _renderAll();

            _log($"Pasted {strokes.Count} stroke(s) and {wells.Count} paint well(s).");
        }

        /// <summary>
        /// Selects all strokes and paint wells in the document.
        /// </summary>
        public void PerformSelectAll()
        {
            var doc = _getDocument();
            _selectionController.ClearSelection();

            // Select all strokes
            var allIndices = Enumerable.Range(0, doc.Strokes.Count);
            _selectionController.SelectStrokes(allIndices);

            // Select all paint wells
            _selectionController.SelectPaintWells(doc.PaintWells.Select(w => w.Id));

            // Switch to Select tool to show the selection
            _setCurrentTool(ToolMode.Select);

            _renderAll();
            _log($"Selected all: {doc.Strokes.Count} stroke(s) and {doc.PaintWells.Count} paint well(s).");
        }

        #endregion

        #region Undo Operations

        /// <summary>
        /// Performs undo operation, removing the last stroke(s) added.
        /// </summary>
        public void PerformUndo()
        {
            var doc = _getDocument();
            if (doc.Strokes.Count == 0) return;

            // Determine how many strokes to undo
            int countToUndo = 1;
            if (_undoGroupSizes.Count > 0)
            {
                countToUndo = _undoGroupSizes.Pop();
            }

            // Remove the strokes (up to the group size or remaining strokes)
            countToUndo = Math.Min(countToUndo, doc.Strokes.Count);

            for (int i = 0; i < countToUndo; i++)
            {
                doc.Strokes.RemoveAt(doc.Strokes.Count - 1);
            }

            _invalidateGcode();
            _renderAll();
        }

        #endregion
    }
}
