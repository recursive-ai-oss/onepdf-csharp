namespace OnePdf.PdfUA;

// Header cells of a table (ISO 14289-1 7.5): when a table's structure is not given by Headers and IDs, its TH cells need a /Scope
// (ISO 32000-2 14.8.4.8.3 describes how a data cell finds its headers). The rule is judged per data cell, the way validators do:
//  - a data cell with a /Headers list naming a cell of the table has its headers;
//  - otherwise a TH with scope Column or Both in one of the data cell's columns, entirely above it, or a TH with scope Row or Both in
//    one of its rows, entirely to its left, is its header; a TH without a scope is never one;
//  - a table is only in breach when it has a TH without a scope and a data cell that finds no header. A table whose TH cells all carry
//    a scope is not examined further, so a data cell outside every header's reach is not reported;
//  - a table whose rows do not all cover the same number of columns is not examined either: it has no grid to assign headers on, and the
//    irregular-rows finding (also 7.5) is the one that applies.
internal sealed partial class PdfUAChecker
{
    private const int MaxGridColumns = 4096;
    private const long TableWorkBudget = 1L << 24;

    private sealed class TableCell
    {
        public int Node;
        public bool Header;
        public int Row;
        public int Col;
        public int RowSpan = 1;
        public int ColSpan = 1;
        public string? Scope;
        public string? Id;
        public bool HasHeaders;
        public List<string>? HeaderIds;
    }

    /// <summary>The result of looking at one table's header cells.</summary>
    private sealed class TableHeaders
    {
        public readonly List<TableCell> Cells = [];
        public readonly List<TableCell> Unscoped = [];
        public int Unconnected;
        public bool Examined;
    }

    /// <summary>
    /// The scope to give each header cell without one, where the answer is plain: a cell in a row made only of header cells heads its
    /// column; the first cell of a row that also holds data cells heads its row. Only tables in breach of the header rule are looked at.
    /// Returns pairs of the element's object number and the scope name (<c>Column</c> or <c>Row</c>).
    /// </summary>
    public List<(int Element, string Scope)> HeaderScopeRepairs()
    {
        var repairs = new List<(int, string)>();
        Walk();
        for (int i = 0; i < _nodes.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (_nodes[i].Role != "Table")
            {
                continue;
            }

            TableHeaders t = ReadHeaders(i);
            if (!t.Examined || t.Unscoped.Count == 0 || t.Unconnected == 0)
            {
                continue;
            }

            var byRow = new Dictionary<int, List<TableCell>>();
            foreach (TableCell c in t.Cells)
            {
                if (!byRow.TryGetValue(c.Row, out List<TableCell>? list))
                {
                    byRow[c.Row] = list = [];
                }

                list.Add(c);
            }

            foreach (TableCell c in t.Unscoped)
            {
                List<TableCell> row = byRow[c.Row];
                string? scope = null;
                if (row.TrueForAll(static x => x.Header))
                {
                    scope = "Column";
                }
                else if (ReferenceEquals(row[0], c) && row.Exists(static x => !x.Header))
                {
                    scope = "Row";
                }

                if (scope is not null && _nodes[c.Node].Number > 0)
                {
                    repairs.Add((_nodes[c.Node].Number, scope));
                }
            }
        }

        return repairs;
    }

    // Reports the header cells of a table in breach of the rule (see the comment at the top of the file).
    private void CheckTableHeaders(int tableIndex)
    {
        TableHeaders t = ReadHeaders(tableIndex);
        if (!t.Examined || t.Unscoped.Count == 0 || t.Unconnected == 0)
        {
            return;
        }

        foreach (TableCell c in t.Unscoped)
        {
            if (Limited("scope"))
            {
                break;
            }

            Add("15", "7.5", $"A header cell (TH) has no /Scope, and {t.Unconnected} data cell{(t.Unconnected == 1 ? string.Empty : "s")} of its table cannot be tied to a header by position or by /Headers (ISO 32000-2 14.8.4.8.3).", _nodes[c.Node].Number);
        }
    }

    private TableHeaders ReadHeaders(int tableIndex)
    {
        var result = new TableHeaders();
        var rows = new List<int>();
        var stack = new Stack<int>();
        Node table = _nodes[tableIndex];
        for (int k = table.Kids.Count - 1; k >= 0; k--)
        {
            stack.Push(table.Kids[k]);
        }

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            Node n = _nodes[i];
            if (n.Role == "TR")
            {
                rows.Add(i);
            }
            else if (n.Role is "THead" or "TBody" or "TFoot")
            {
                for (int k = n.Kids.Count - 1; k >= 0; k--)
                {
                    stack.Push(n.Kids[k]);
                }
            }
        }

        // Place every cell on a grid, as the width check does: a cell takes the next column no row span from above still covers.
        var carry = new List<int>();
        int width = 0;
        long work = 0;
        int expectedWidth = -1;
        bool regular = true;
        for (int r = 0; r < rows.Count; r++)
        {
            int col = 0;
            foreach (int c in _nodes[rows[r]].Kids)
            {
                Node cellNode = _nodes[c];
                if (cellNode.Role is not ("TH" or "TD"))
                {
                    continue;
                }

                while (col < carry.Count && carry[col] > 0)
                {
                    col++;
                }

                int colSpan = Math.Clamp(TableAttributeInt(cellNode.Dict, "ColSpan"), 1, MaxGridColumns);
                int rowSpan = Math.Clamp(TableAttributeInt(cellNode.Dict, "RowSpan"), 1, MaxGridColumns);
                if (col >= MaxGridColumns)
                {
                    return result; // wider than anything real; give up quietly
                }

                colSpan = Math.Min(colSpan, MaxGridColumns - col);
                var cell = new TableCell
                {
                    Node = c,
                    Header = cellNode.Role == "TH",
                    Row = r,
                    Col = col,
                    RowSpan = rowSpan,
                    ColSpan = colSpan,
                    Id = cellNode.Dict.GetString("ID")?.GetText(),
                };
                if (cell.Header)
                {
                    cell.Scope = TableAttribute(cellNode.Dict, "Scope") is PdfName sn && sn.Value is "Row" or "Column" or "Both" ? sn.Value : null;
                }
                else if (TableAttribute(cellNode.Dict, "Headers") is PdfArray ha)
                {
                    cell.HasHeaders = true;
                    cell.HeaderIds = [];
                    for (int h = 0; h < ha.Count && h < 256; h++)
                    {
                        if (ha[h] is PdfString hs)
                        {
                            cell.HeaderIds.Add(hs.GetText());
                        }
                    }
                }

                result.Cells.Add(cell);
                if (cell.Header && cell.Scope is null)
                {
                    result.Unscoped.Add(cell);
                }

                while (carry.Count < col + colSpan)
                {
                    carry.Add(0);
                }

                for (int s = 0; s < colSpan; s++)
                {
                    carry[col + s] = Math.Max(carry[col + s], rowSpan);
                }

                col += colSpan;
                width = Math.Max(width, col);
                work += colSpan;
            }

            // The row is as wide as its last cell, or as the last column a row span from above still covers here.
            int rowWidth = col;
            for (int k = carry.Count - 1; k >= col; k--)
            {
                if (carry[k] > 0)
                {
                    rowWidth = k + 1;
                    break;
                }
            }

            if (expectedWidth < 0)
            {
                expectedWidth = rowWidth;
            }
            else if (rowWidth != expectedWidth)
            {
                regular = false;
            }

            for (int k = 0; k < carry.Count; k++)
            {
                if (carry[k] > 0)
                {
                    carry[k]--;
                }
            }
        }

        if (!regular)
        {
            // The rows do not form a grid, so no header can be assigned by position; the width finding says so (ISO 14289-1 7.5).
            return result;
        }

        if (result.Unscoped.Count == 0)
        {
            result.Examined = true;
            return result;
        }

        // The first row after which a column (or the first column before which a row) has a header that reaches a data cell.
        var colHeaderEnd = new int[width];
        Array.Fill(colHeaderEnd, int.MaxValue);
        var rowHeaderEnd = new int[rows.Count];
        Array.Fill(rowHeaderEnd, int.MaxValue);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (TableCell c in result.Cells)
        {
            if (c.Id is { Length: > 0 })
            {
                ids.Add(c.Id);
            }

            if (!c.Header || c.Scope is null)
            {
                continue;
            }

            if (c.Scope is "Column" or "Both")
            {
                for (int k = c.Col; k < Math.Min(c.Col + c.ColSpan, width); k++)
                {
                    colHeaderEnd[k] = Math.Min(colHeaderEnd[k], c.Row + c.RowSpan);
                }

                work += c.ColSpan;
            }

            if (c.Scope is "Row" or "Both")
            {
                for (int k = c.Row; k < Math.Min(c.Row + c.RowSpan, rows.Count); k++)
                {
                    rowHeaderEnd[k] = Math.Min(rowHeaderEnd[k], c.Col + c.ColSpan);
                }

                work += c.RowSpan;
            }

            if (work > TableWorkBudget)
            {
                return result;
            }
        }

        foreach (TableCell c in result.Cells)
        {
            if (c.Header)
            {
                continue;
            }

            bool connected = false;
            if (c.HasHeaders)
            {
                foreach (string id in c.HeaderIds!)
                {
                    connected |= ids.Contains(id);
                }
            }

            for (int k = c.Col; !connected && k < Math.Min(c.Col + c.ColSpan, width); k++)
            {
                connected = colHeaderEnd[k] <= c.Row;
            }

            for (int k = c.Row; !connected && k < Math.Min(c.Row + c.RowSpan, rows.Count); k++)
            {
                connected = rowHeaderEnd[k] <= c.Col;
            }

            work += c.ColSpan + c.RowSpan;
            if (work > TableWorkBudget)
            {
                return result;
            }

            if (!connected)
            {
                result.Unconnected++;
            }
        }

        result.Examined = true;
        return result;
    }

    // One value of the Table attribute objects of a structure element (ISO 32000-1 14.8.5.7): from its own /A, then from the classes it
    // names with /C. A value in the element's own attributes wins.
    private PdfObject? TableAttribute(PdfDictionary element, string key)
    {
        PdfObject? found = null;
        void Look(PdfObject? o, int depth)
        {
            if (found is not null || depth > 3)
            {
                return;
            }

            if (o is PdfArray arr)
            {
                for (int i = 0; i < arr.Count && i < 64 && found is null; i++)
                {
                    Look(arr[i], depth + 1);
                }
            }
            else if (o is PdfDictionary d && d.GetName("O") == "Table")
            {
                found = d.Get(key);
            }
        }

        Look(element.Get("A"), 0);
        if (found is null && _doc.Catalog.GetDictionary("StructTreeRoot")?.GetDictionary("ClassMap") is { } classMap)
        {
            PdfObject? c = element.Get("C");
            if (c is PdfName one)
            {
                Look(classMap.Get(one.Value), 0);
            }
            else if (c is PdfArray names)
            {
                for (int i = 0; i < names.Count && i < 64 && found is null; i++)
                {
                    if (names[i] is PdfName n)
                    {
                        Look(classMap.Get(n.Value), 0);
                    }
                }
            }
        }

        return found;
    }

    private int TableAttributeInt(PdfDictionary element, string key) => TableAttribute(element, key) is PdfInteger i && i.Value is >= 1 and <= MaxGridColumns ? (int)i.Value : 1;
}
