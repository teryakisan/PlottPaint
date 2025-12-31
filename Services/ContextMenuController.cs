using NVSPlotter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Event arguments for subdivision request.
    /// </summary>
    public sealed class SubdivisionRequestedEventArgs : EventArgs
    {
        public List<int> SelectedIndices { get; }

        public SubdivisionRequestedEventArgs(List<int> selectedIndices)
        {
            SelectedIndices = selectedIndices;
        }
    }

    /// <summary>
    /// Contains the enabled state for all context menu items.
    /// </summary>
    public sealed class ContextMenuState
    {
        public bool CutEnabled { get; set; }
        public bool CopyEnabled { get; set; }
        public bool PasteEnabled { get; set; }
        public bool DeleteEnabled { get; set; }
        public bool SelectAllEnabled { get; set; }
        public bool DeselectEnabled { get; set; }
        public bool GroupEnabled { get; set; }
        public bool UngroupEnabled { get; set; }
        public bool SeparateEnabled { get; set; }
        public bool SubdivideEnabled { get; set; }
        public bool ToggleIntermediatePointsEnabled { get; set; }
        public bool ToggleIntermediatePointsChecked { get; set; }
    }

    /// <summary>
    /// Handles context menu operations including group/ungroup, clipboard delegations,
    /// and subdivision initiation.
    /// </summary>
    public sealed class ContextMenuController
    {
        private readonly Func<PlotDocument> _getDocument;
        private readonly SelectionController _selectionController;
        private readonly ClipboardService _clipboardService;
        private readonly Action _renderAll;
        private readonly Action<string> _log;
        private readonly Action _invalidateGcode;
        private readonly HashSet<Guid> _showIntermediatePointsForGroups;

        /// <summary>
        /// Raised when subdivision is requested. MainWindow handles the complex state management.
        /// </summary>
        public event EventHandler<SubdivisionRequestedEventArgs>? SubdivisionRequested;

        public ContextMenuController(
            Func<PlotDocument> getDocument,
            SelectionController selectionController,
            ClipboardService clipboardService,
            Action renderAll,
            Action<string> log,
            Action invalidateGcode,
            HashSet<Guid> showIntermediatePointsForGroups)
        {
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _selectionController = selectionController ?? throw new ArgumentNullException(nameof(selectionController));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _renderAll = renderAll ?? throw new ArgumentNullException(nameof(renderAll));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _invalidateGcode = invalidateGcode ?? throw new ArgumentNullException(nameof(invalidateGcode));
            _showIntermediatePointsForGroups = showIntermediatePointsForGroups ?? throw new ArgumentNullException(nameof(showIntermediatePointsForGroups));
        }

        #region Menu State Management

        /// <summary>
        /// Gets the current enabled/checked state for all context menu items.
        /// </summary>
        public ContextMenuState GetMenuState()
        {
            var hasSelection = _selectionController.HasSelection;
            var hasClipboard = _clipboardService.HasClipboardData();
            var doc = _getDocument();
            var hasContent = doc.Strokes.Count > 0 || doc.PaintWells.Count > 0;

            // Check if selection contains grouped strokes
            var hasGroupedStrokes = false;
            var anyGroupShowingIntermediate = false;
            var hasMultipleStrokes = _selectionController.SelectedIndices.Count > 1;

            if (hasSelection)
            {
                var selectedGroupIds = new HashSet<Guid>();
                foreach (var idx in _selectionController.SelectedIndices)
                {
                    if (idx >= 0 && idx < doc.Strokes.Count)
                    {
                        var stroke = doc.Strokes[idx];
                        if (stroke.GroupId.HasValue)
                        {
                            selectedGroupIds.Add(stroke.GroupId.Value);
                            hasGroupedStrokes = true;
                        }
                    }
                }

                // Find ALL descendant groups recursively for intermediate points display check
                var allGroupIds = GetAllDescendantGroupIds(selectedGroupIds, doc);
                anyGroupShowingIntermediate = allGroupIds.Any(id => _showIntermediatePointsForGroups.Contains(id));
            }

            return new ContextMenuState
            {
                CutEnabled = hasSelection,
                CopyEnabled = hasSelection,
                PasteEnabled = hasClipboard,
                DeleteEnabled = hasSelection,
                SelectAllEnabled = hasContent,
                DeselectEnabled = hasSelection,
                GroupEnabled = hasMultipleStrokes,
                UngroupEnabled = hasGroupedStrokes,
                SeparateEnabled = hasGroupedStrokes,
                SubdivideEnabled = hasSelection && _selectionController.SelectedIndices.Count > 0,
                ToggleIntermediatePointsEnabled = hasGroupedStrokes,
                ToggleIntermediatePointsChecked = anyGroupShowingIntermediate
            };
        }

        #endregion

        #region Clipboard Operations (Delegates)

        /// <summary>
        /// Performs cut operation on selected items.
        /// </summary>
        public void PerformCut()
        {
            if (!_selectionController.HasSelection)
            {
                _log("Nothing selected to cut.");
                return;
            }

            var strokes = _selectionController.GetSelectedStrokes();
            var wells = _selectionController.GetSelectedPaintWells();
            var bounds = _selectionController.SelectionBounds;
            _clipboardService.CopyToClipboard(strokes, wells, bounds);

            _selectionController.DeleteSelection();
            _invalidateGcode();

            _log($"Cut {strokes.Count} stroke(s) and {wells.Count} paint well(s) to clipboard.");
        }

        /// <summary>
        /// Performs copy operation on selected items.
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
        /// Performs paste operation from clipboard.
        /// </summary>
        public void PerformPaste(Action updatePaintWellsUI, Action<ToolMode> setCurrentTool)
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

            var startIndex = doc.Strokes.Count;
            foreach (var stroke in strokes)
            {
                doc.Strokes.Add(stroke);
            }

            foreach (var well in wells)
            {
                doc.PaintWells.Add(well);
            }

            _selectionController.ClearSelection();
            var indices = Enumerable.Range(startIndex, strokes.Count).ToList();
            _selectionController.SelectStrokes(indices);
            _selectionController.SelectPaintWells(wells.Select(w => w.Id));

            setCurrentTool(ToolMode.Select);

            _invalidateGcode();
            updatePaintWellsUI();
            _renderAll();

            _log($"Pasted {strokes.Count} stroke(s) and {wells.Count} paint well(s).");
        }

        /// <summary>
        /// Deletes selected items.
        /// </summary>
        public void PerformDelete()
        {
            if (_selectionController.HasSelection)
            {
                _selectionController.DeleteSelection();
                _invalidateGcode();
                _log("Deleted selected items.");
            }
        }

        /// <summary>
        /// Selects all strokes and paint wells.
        /// </summary>
        public void PerformSelectAll(Action<ToolMode> setCurrentTool)
        {
            var doc = _getDocument();
            _selectionController.ClearSelection();

            var allIndices = Enumerable.Range(0, doc.Strokes.Count);
            _selectionController.SelectStrokes(allIndices);
            _selectionController.SelectPaintWells(doc.PaintWells.Select(w => w.Id));

            setCurrentTool(ToolMode.Select);

            _renderAll();
            _log($"Selected all: {doc.Strokes.Count} stroke(s) and {doc.PaintWells.Count} paint well(s).");
        }

        /// <summary>
        /// Clears the current selection.
        /// </summary>
        public void PerformDeselect()
        {
            _selectionController.ClearSelection();
            _log("Selection cleared.");
        }

        #endregion

        #region Group Operations

        /// <summary>
        /// Groups selected strokes into a single object.
        /// </summary>
        public void PerformGroup()
        {
            if (!_selectionController.HasSelection || _selectionController.SelectedIndices.Count < 2)
            {
                _log("Select at least 2 strokes to group.");
                return;
            }

            var doc = _getDocument();
            var selectedIndices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            var newGroupId = Guid.NewGuid();
            var count = 0;

            for (int i = 0; i < selectedIndices.Count; i++)
            {
                var idx = selectedIndices[i];
                if (idx >= 0 && idx < doc.Strokes.Count)
                {
                    var stroke = doc.Strokes[idx];
                    doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                    {
                        PaintWellId = stroke.PaintWellId,
                        GroupId = newGroupId,
                        IsGroupStart = i == 0,
                        IsGroupEnd = i == selectedIndices.Count - 1
                    };
                    count++;
                }
            }

            _invalidateGcode();
            _renderAll();
            _log($"Grouped {count} stroke(s) into a single object.");
        }

        /// <summary>
        /// Ungroups selected strokes into individual objects.
        /// </summary>
        public void PerformUngroup()
        {
            if (!_selectionController.HasSelection)
            {
                _log("No strokes selected to ungroup.");
                return;
            }

            var doc = _getDocument();
            var selectedIndices = _selectionController.SelectedIndices.ToList();
            var ungroupedCount = 0;

            foreach (var idx in selectedIndices)
            {
                if (idx >= 0 && idx < doc.Strokes.Count)
                {
                    var stroke = doc.Strokes[idx];
                    if (stroke.GroupId.HasValue)
                    {
                        doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                        {
                            PaintWellId = stroke.PaintWellId,
                            GroupId = null,
                            IsGroupStart = false,
                            IsGroupEnd = false
                        };
                        ungroupedCount++;
                    }
                }
            }

            if (ungroupedCount > 0)
            {
                _invalidateGcode();
                _renderAll();
                _log($"Ungrouped {ungroupedCount} stroke(s) into individual objects.");
            }
            else
            {
                _log("Selected strokes are not part of any group.");
            }
        }

        /// <summary>
        /// Separates selected strokes from their group into a standalone object.
        /// </summary>
        public void PerformSeparateFromGroup()
        {
            if (!_selectionController.HasSelection)
            {
                _log("No strokes selected to separate.");
                return;
            }

            if (_selectionController.SeparateFromGroup())
            {
                _invalidateGcode();
                var count = _selectionController.SelectedIndices.Count;
                _log($"Separated {count} stroke(s) into a standalone object.");
            }
            else
            {
                _log("Selected strokes are not part of a group (already standalone).");
            }
        }

        #endregion

        #region Intermediate Points

        /// <summary>
        /// Toggles the display of intermediate points for selected groups.
        /// </summary>
        public void ToggleIntermediatePoints()
        {
            if (!_selectionController.HasSelection) return;

            var doc = _getDocument();

            // Get all unique GroupIds from selected strokes
            var selectedGroupIds = new HashSet<Guid>();
            foreach (var idx in _selectionController.SelectedIndices)
            {
                if (idx >= 0 && idx < doc.Strokes.Count)
                {
                    var stroke = doc.Strokes[idx];
                    if (stroke.GroupId.HasValue)
                    {
                        selectedGroupIds.Add(stroke.GroupId.Value);
                    }
                }
            }

            if (selectedGroupIds.Count == 0)
            {
                _log("No grouped strokes in selection.");
                return;
            }

            // Find ALL descendant groups recursively
            var allGroupIds = GetAllDescendantGroupIds(selectedGroupIds, doc);

            // Check if any are currently showing intermediate points
            var anyShowing = allGroupIds.Any(id => _showIntermediatePointsForGroups.Contains(id));

            if (anyShowing)
            {
                foreach (var groupId in allGroupIds)
                {
                    _showIntermediatePointsForGroups.Remove(groupId);
                }
                _log($"Hidden intermediate points for {allGroupIds.Count} group(s).");
            }
            else
            {
                foreach (var groupId in allGroupIds)
                {
                    _showIntermediatePointsForGroups.Add(groupId);
                }
                _log($"Showing intermediate points for {allGroupIds.Count} group(s).");
            }

            _renderAll();
        }

        #endregion

        #region Subdivision

        /// <summary>
        /// Initiates subdivision mode for selected strokes.
        /// Raises SubdivisionRequested event for MainWindow to handle.
        /// </summary>
        public void RequestSubdivision()
        {
            if (!_selectionController.HasSelection)
            {
                _log("No strokes selected to subdivide.");
                return;
            }

            var indices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            if (indices.Count == 0)
            {
                _log("No strokes selected to subdivide.");
                return;
            }

            SubdivisionRequested?.Invoke(this, new SubdivisionRequestedEventArgs(indices));
        }

        #endregion

        #region Group Hierarchy Helpers

        /// <summary>
        /// Recursively finds all descendant group IDs for the given parent group IDs.
        /// </summary>
        public static HashSet<Guid> GetAllDescendantGroupIds(IEnumerable<Guid> parentGroupIds, PlotDocument doc)
        {
            var result = new HashSet<Guid>(parentGroupIds);
            var toProcess = new Queue<Guid>(parentGroupIds);

            while (toProcess.Count > 0)
            {
                var currentParent = toProcess.Dequeue();

                foreach (var stroke in doc.Strokes)
                {
                    if (stroke.ParentGroupId == currentParent && stroke.GroupId.HasValue)
                    {
                        var childGroupId = stroke.GroupId.Value;
                        if (result.Add(childGroupId))
                        {
                            toProcess.Enqueue(childGroupId);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Checks if the given group ID or any of its ancestors have intermediate points enabled.
        /// </summary>
        public bool HasAncestorWithIntermediatePointsEnabled(Guid groupId, PlotDocument doc)
        {
            if (_showIntermediatePointsForGroups.Contains(groupId))
                return true;

            var parentGroupId = doc.Strokes
                .FirstOrDefault(s => s.GroupId == groupId)?.ParentGroupId;

            var visited = new HashSet<Guid> { groupId };
            while (parentGroupId.HasValue && !visited.Contains(parentGroupId.Value))
            {
                if (_showIntermediatePointsForGroups.Contains(parentGroupId.Value))
                    return true;

                visited.Add(parentGroupId.Value);
                parentGroupId = doc.Strokes
                    .FirstOrDefault(s => s.GroupId == parentGroupId.Value)?.ParentGroupId;
            }

            return false;
        }

        #endregion
    }
}
