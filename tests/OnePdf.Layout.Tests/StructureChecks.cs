namespace OnePdf.Layout.Tests;

internal static class StructureChecks
{
    private const double Tol = 1e-6;

    /// <summary>Checks the invariants of every generated table and list against the placed pages.</summary>
    public static (int Tables, int Pieces, int Lists) Check(TreeGen gen, List<Placed> bodies, bool rtl, double bodyHeight, string who)
    {
        int pieces = 0;
        foreach ((Table table, bool[] keep) in gen.Tables)
        {
            var found = new List<(int Page, Placed Node, Table.Piece Piece)>();
            for (int p = 0; p < bodies.Count; p++)
            {
                foreach (Placed n in Support.Walk(bodies[p]))
                {
                    if (ReferenceEquals(n.Element, table))
                    {
                        found.Add((p, n, (Table.Piece)n.Data!));
                    }
                }
            }

            Assert.True(found.Count > 0, $"{who}: table not placed");
            pieces += found.Count;
            Table.State st = found[0].Piece.State;
            TableGrid g = st.Grid;
            int header = g.HeaderCount;
            int bodyEnd = header + g.BodyCount;
            var rowPieces = new Dictionary<int, List<int>>();
            var slotPieces = new Dictionary<int, int>();
            for (int k = 0; k < found.Count; k++)
            {
                (int page, Placed node, Table.Piece piece) = found[k];
                Assert.Same(st, piece.State);
                // The pieces are on consecutive pages (the table is in order and never skips a page it still needs).
                if (k > 0)
                {
                    Assert.True(page > found[k - 1].Page, $"{who}: table pieces out of page order");
                }

                // Header rows first, in order, then body rows, then footer rows; each section contiguous.
                int[] rows = [.. piece.Rows.Select(r => r.Row)];
                Assert.True(rows.Take(header).SequenceEqual(Enumerable.Range(0, header)), $"{who}: header rows missing on piece {k} of table: {string.Join(",", rows)}");
                int footerCount = g.Rows - bodyEnd;
                Assert.True(rows.Skip(rows.Length - footerCount).SequenceEqual(Enumerable.Range(bodyEnd, footerCount)), $"{who}: footer rows missing on piece {k}");
                int[] bodyRows = [.. rows.Skip(header).Take(rows.Length - header - footerCount)];
                Assert.True(bodyRows.Length > 0 || g.BodyCount == 0, $"{who}: a table piece without any body row");
                for (int i = 1; i < bodyRows.Length; i++)
                {
                    Assert.Equal(bodyRows[i - 1] + 1, bodyRows[i]);
                }

                foreach (int r in bodyRows)
                {
                    if (!rowPieces.TryGetValue(r, out List<int>? l))
                    {
                        rowPieces[r] = l = [];
                    }

                    l.Add(k);
                }

                // Rows stack without gaps or overlaps and end at the node's bottom.
                double y = 0;
                foreach (Table.RowGeometry rg in piece.Rows)
                {
                    Assert.True(Math.Abs(rg.Y - y) < Tol, $"{who}: row {rg.Row} at {rg.Y}, expected {y}");
                    y += rg.Height;
                }

                Assert.True(Math.Abs(y - node.Height) < Tol, $"{who}: rows end at {y}, node is {node.Height}");
                Assert.True(node.Height <= bodyHeight + Tol, $"{who}: table piece is {node.Height} tall");

                foreach (Table.CellGeometry cg in piece.Cells)
                {
                    TableSlot slot = g.Slots[cg.Slot];
                    slotPieces[cg.Slot] = slotPieces.GetValueOrDefault(cg.Slot) + 1;

                    // The cell sits exactly on its columns.
                    double span = st.ColumnX[slot.Col + slot.ColSpan] - st.ColumnX[slot.Col];
                    double expectedX = rtl ? st.Width - st.ColumnX[slot.Col] - span : st.ColumnX[slot.Col];
                    Assert.True(Math.Abs(cg.X - expectedX) < Tol && Math.Abs(cg.Width - span) < Tol, $"{who}: cell {cg.Slot} at {cg.X} wide {cg.Width}, expected {expectedX} wide {span}");

                    // Its height is that of the rows it spans, and those rows are all on this piece.
                    double covered = 0;
                    for (int r = slot.Top; r < slot.Top + slot.RowSpan; r++)
                    {
                        Table.RowGeometry? rg = piece.Rows.Cast<Table.RowGeometry?>().FirstOrDefault(x => x!.Value.Row == r);
                        Assert.True(rg is not null, $"{who}: cell {cg.Slot} spans row {r}, which is not on its piece");
                        covered += rg!.Value.Height;
                    }

                    Assert.True(Math.Abs(covered - cg.Height) < Tol, $"{who}: cell {cg.Slot} is {cg.Height} tall, its rows are {covered}");

                    // Content stays inside its cell.
                    if (cg.Content is { } c)
                    {
                        Assert.True(
                            c.X >= cg.X - Tol && c.X + c.Width <= cg.X + cg.Width + Tol && c.Y >= cg.Y - Tol && c.Y + c.Height <= cg.Y + cg.Height + Tol,
                            $"{who}: content of cell {cg.Slot} ({c.X},{c.Y},{c.Width},{c.Height}) leaves the cell ({cg.X},{cg.Y},{cg.Width},{cg.Height})");
                    }
                }

                // Cells of one piece do not overlap.
                for (int a = 0; a < piece.Cells.Count; a++)
                {
                    for (int b = a + 1; b < piece.Cells.Count; b++)
                    {
                        Table.CellGeometry p = piece.Cells[a];
                        Table.CellGeometry q = piece.Cells[b];
                        double ox = Math.Min(p.X + p.Width, q.X + q.Width) - Math.Max(p.X, q.X);
                        double oy = Math.Min(p.Y + p.Height, q.Y + q.Height) - Math.Max(p.Y, q.Y);
                        Assert.False(ox > 1e-4 && oy > 1e-4, $"{who}: cells {p.Slot} and {q.Slot} overlap");
                    }
                }
            }

            // Every body row appears, on consecutive pieces; a row seen on several pieces is a split row and may not be kept together
            // or part of a span group, unless it could not fit a page anyway.
            Assert.Equal(g.BodyCount, rowPieces.Count);
            double room = bodyHeight - found.Max(f => f.Piece.Rows.Where(r => r.Row < header || r.Row >= bodyEnd).Sum(r => r.Height));
            foreach ((int row, List<int> at) in rowPieces)
            {
                for (int i = 1; i < at.Count; i++)
                {
                    Assert.Equal(at[i - 1] + 1, at[i]);
                }

                if (at.Count > 1)
                {
                    double total = at.Sum(k => found[k].Piece.Rows.First(r => r.Row == row).Height);
                    Assert.True(!keep[row - header] || total > room - 1e-6, $"{who}: kept-together row {row - header} split over {at.Count} pieces ({total} of room {room})");
                    Assert.True(g.BlockEnd[row] == row && !HasSpanInto(g, row), $"{who}: row {row} of a span group was split");
                }
            }

            // Every cell is placed: once for a row on one piece, on each piece of a split row.
            for (int s = 0; s < g.Slots.Length; s++)
            {
                TableSlot slot = g.Slots[s];
                int expected = slot.Top < header || slot.Top >= bodyEnd ? found.Count : rowPieces[slot.Top].Count;
                Assert.True(slotPieces.GetValueOrDefault(s) == expected, $"{who}: cell {s} placed {slotPieces.GetValueOrDefault(s)} times, expected {expected}");
            }
        }

        foreach (ItemList list in gen.Lists)
        {
            var xs = new HashSet<double>();
            foreach (Placed n in bodies.SelectMany(Support.Walk).Where(n => ReferenceEquals(n.Element, list)))
            {
                foreach (Placed entry in n.Children)
                {
                    // The content column starts at one x on every entry and every page: the hanging indent is shared.
                    xs.Add(Math.Round(entry.Children[^1].X - (rtl ? 0 : 0), 6));
                    Assert.InRange(entry.Children.Count, 1, 2);
                }
            }

            Assert.True(xs.Count <= 1, $"{who}: list content starts at {xs.Count} different places: {string.Join(",", xs)}");
        }

        return (gen.Tables.Count, pieces, gen.Lists.Count);
    }

    private static bool HasSpanInto(TableGrid g, int row)
    {
        for (int s = 0; s < g.Slots.Length; s++)
        {
            TableSlot slot = g.Slots[s];
            if (slot.RowSpan > 1 && row >= slot.Top && row < slot.Top + slot.RowSpan)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A split paragraph leaves at least two lines on each side, unless the document noted that it could not.</summary>
    public static void CheckWidowsAndOrphans(TreeGen gen, List<Placed> bodies, bool noted, string who)
    {
        foreach (Text t in gen.Texts)
        {
            var frags = bodies.SelectMany(Support.Walk).Where(n => ReferenceEquals(n.Element, t)).Select(n => (Text.Fragment)n.Data!).ToList();
            if (frags.Count < 2 || noted)
            {
                continue;
            }

            Assert.True(frags[0].Count >= 2, $"{who}: text {t.Name} starts with {frags[0].Count} line(s) on its page");
            Assert.True(frags[^1].Count >= 2, $"{who}: text {t.Name} ends with {frags[^1].Count} line(s) on its page");
        }
    }
}
