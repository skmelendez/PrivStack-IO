// ============================================================================
// File: ITableGridDataSource.cs
// Description: Data source interface for the TableGrid control. Each table type
//              (dataset, view, plugin-query, source-backed, manual, entity)
//              implements this to bridge domain-specific logic to the generic grid.
// ============================================================================

namespace PrivStack.UI.Adaptive.Models;

public interface ITableGridDataSource
{
    bool IsEditable { get; }
    bool SupportsRowReorder { get; }
    bool SupportsColumnReorder { get; }
    bool SupportsStructureEditing { get; }
    bool SupportsSorting { get; }

    Task<(TableGridData Data, TablePagingInfo Paging)> GetDataAsync(
        string? filterText, int sortColumnIndex,
        TableSortDirection sortDirection, int currentPage, int pageSize);

    Task OnCellEditedAsync(string rowId, int columnIndex, string newValue);
    Task OnRowReorderedAsync(int fromIndex, int toIndex);
    Task OnColumnReorderedAsync(int fromIndex, int toIndex);
    void OnColumnWidthsChanged(IReadOnlyList<double> pixelWidths);
    Task OnAddRowAsync();
    Task OnRemoveRowAsync();
    Task OnAddColumnAsync();
    Task OnRemoveColumnAsync();
    Task OnToggleHeaderAsync(bool isHeader);
    Task OnColumnAlignmentChangedAsync(int columnIndex, TableColumnAlignment alignment);
    Task<TableGridData?> GetFullDataForExportAsync();
}
