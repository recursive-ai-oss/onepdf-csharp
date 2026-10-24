namespace OnePdf.Layout.Tests;

// Random tables and lists for the generated-tree property tests. Cells of body rows hold registered atoms (spacers) or registered text,
// so the generic checks see them; header and footer cells repeat on every page, so they hold text that is not registered.
internal sealed partial class TreeGen
{
    public List<(Table Table, bool[] KeepTogether)> Tables { get; } = [];

    public List<ItemList> Lists { get; } = [];

    private Text BodyText(int maxWords)
    {
        var t = new Text { Content = Sentence(1 + Pick(maxWords)), Size = 8 + Pick(4), Name = "T" + _id++ };
        Texts.Add(t);
        return t;
    }

    private Table TableNode(double w)
    {
        int cols = 2 + Pick(3);
        var table = new Table
        {
            CellPadding = Insets.All(Pick(5)),
            CellBorder = Pick(3) == 0 ? default : new Border(0.5 + Pick(3), LayoutColor.Black, (BorderStyle)Pick(3)),
        };
        for (int c = 0; c < cols; c++)
        {
            table.Columns.Add(Pick(4) switch
            {
                0 => TableColumn.Constant(20 + Pick(40)),
                1 => TableColumn.Relative(1 + Pick(3)),
                _ => TableColumn.Auto(),
            });
        }

        int header = Pick(3);
        int footer = Pick(2);
        int body = 2 + Pick(30);
        if (Pick(2) == 0)
        {
            table.RowBackground = i => i % 2 == 0 ? new LayoutColor(0.95, 0.95, 0.95) : null;
        }

        FillSection(table.HeaderRows, header, cols, atomic: false, repeated: true);
        FillSection(table.Rows, body, cols, atomic: false, repeated: false);
        FillSection(table.FooterRows, footer, cols, atomic: false, repeated: true);
        var keep = new bool[body];
        for (int i = 0; i < body; i++)
        {
            keep[i] = table.Rows[i].KeepTogether;
        }

        Tables.Add((table, keep));
        return table;
    }

    // Rows are filled by simulating the table's own placement, so spans never overlap. Rows that take part in a row span hold only atoms,
    // because such rows cannot split and must fit a page.
    private void FillSection(List<TableRow> rows, int count, int cols, bool atomic, bool repeated)
    {
        var occupied = new int[cols];
        bool[] groupRow = new bool[count + 1];
        for (int r = 0; r < count; r++)
        {
            groupRow[r] = !repeated && r + 1 < count && Pick(5) == 0;
        }

        for (int r = 0; r < count; r++)
        {
            var row = new TableRow { KeepTogether = !repeated && Pick(5) == 0, Background = Pick(6) == 0 ? new LayoutColor(1, 0.95, 0.8) : null };
            bool inGroup = groupRow[r] || (r > 0 && groupRow[r - 1]);
            int c = 0;
            while (c < cols)
            {
                if (occupied[c] > 0)
                {
                    c++;
                    continue;
                }

                int free = 1;
                while (c + free < cols && occupied[c + free] == 0)
                {
                    free++;
                }

                int cs = Pick(4) == 0 ? 1 + Pick(free) : 1;
                int rs = groupRow[r] && Pick(2) == 0 ? 2 : 1;
                Element content;
                if (repeated)
                {
                    content = new Text { Content = Sentence(1 + Pick(5)), Size = 9 };
                }
                else if (inGroup || atomic || Pick(3) == 0)
                {
                    double h = Next(5, 40);
                    content = new Spacer { Height = h, Name = Atom("A", h) };
                }
                else
                {
                    content = BodyText(25);
                }

                row.Cells.Add(new TableCell(content)
                {
                    ColumnSpan = cs,
                    RowSpan = rs,
                    Background = Pick(8) == 0 ? new LayoutColor(0.9, 1, 0.9) : null,
                    VerticalAlignment = (VerticalAlignment)Pick(3),
                    Padding = Pick(6) == 0 ? Insets.All(Pick(8)) : null,
                });
                for (int k = 0; k < cs; k++)
                {
                    occupied[c + k] = rs;
                }

                c += cs;
                // A cell spanning rows in the last row of a section is cut back by the table; keep the simulation honest.
                if (r + rs > count)
                {
                    rs = count - r;
                }
            }

            for (int k = 0; k < cols; k++)
            {
                if (occupied[k] > 0)
                {
                    occupied[k]--;
                }
            }

            rows.Add(row);
        }
    }

    private ItemList ListNode(int depth, double w)
    {
        var list = new ItemList
        {
            Kind = (ListKind)Pick(2),
            NumberStyle = (NumberingStyle)Pick(5),
            Start = 1 + Pick(20),
            Size = 10,
            Spacing = Pick(3) * 2,
            MarkerGap = 4 + Pick(6),
        };
        int n = 2 + Pick(8);
        for (int i = 0; i < n; i++)
        {
            Element content = Pick(4) switch
            {
                0 => AtomSpacer(),
                1 when depth < 3 && w > 120 => new Column { Items = { BodyText(10), ListNode(depth + 1, w - 40) } },
                _ => BodyText(40),
            };
            list.Items.Add(new ListItem(content));
        }

        Lists.Add(list);
        return list;
    }

    private Spacer AtomSpacer()
    {
        double h = Next(5, 40);
        return new Spacer { Height = h, Name = Atom("A", h) };
    }
}
