using System.Runtime.InteropServices;

namespace PrivStack.Desktop.Native;

/// <summary>
/// P/Invoke bindings for dataset FFI functions.
/// All returned nint pointers must be freed with NativeLibrary.FreeString().
/// </summary>
internal static partial class DatasetNativeLibrary
{
    private const string LibraryName = "privstack_ffi";

    // ── Phase 1: Core CRUD ──────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_import_csv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ImportCsv(string filePath, string name);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_list")]
    public static partial nint List();

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_get", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Get(string datasetId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_delete", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError Delete(string datasetId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_rename", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError Rename(string datasetId, string newName);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_query", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Query(string queryJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_get_columns", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetColumns(string datasetId);

    // ── Phase 5: Relations ──────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_create_relation", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateRelation(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_delete_relation", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError DeleteRelation(string relationId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_list_relations", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ListRelations(string datasetId);

    // ── Phase 6: Row-Page Linking ───────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_link_row_page", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError LinkRowPage(string datasetId, string rowKey, string pageId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_get_page_for_row", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetPageForRow(string datasetId, string rowKey);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_get_row_for_page", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetRowForPage(string pageId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_unlink_row_page", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError UnlinkRowPage(string datasetId, string rowKey);

    // ── Phase 8: Views ──────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_create_view", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateView(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_update_view", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError UpdateView(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_delete_view", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError DeleteView(string viewId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_list_views", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ListViews(string datasetId);

    // ── Phase 9: Aggregation ────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_aggregate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Aggregate(string requestJson);

    // ── Phase 10: Raw SQL & Saved Queries ───────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_execute_sql", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ExecuteSql(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_create_saved_query", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateSavedQuery(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_update_saved_query", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError UpdateSavedQuery(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_delete_saved_query", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError DeleteSavedQuery(string queryId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_list_saved_queries")]
    public static partial nint ListSavedQueries();

    // ── SQL v2 + Mutations ───────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_execute_sql_v2", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ExecuteSqlV2(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_create_empty", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateEmpty(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_duplicate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Duplicate(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_import_content", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ImportContent(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_insert_row", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint InsertRow(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_update_cell", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint UpdateCell(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_delete_rows", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint DeleteRows(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_add_column", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AddColumn(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_drop_column", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError DropColumn(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_rename_column", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError RenameColumn(string requestJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_dataset_alter_column_type", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AlterColumnType(string requestJson);
}
