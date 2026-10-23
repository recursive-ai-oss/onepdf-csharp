#!/usr/bin/env python3
"""Writes testdata/generated/pdf/lo-form-fields.pdf: a LibreOffice Writer page with one of each form control, exported as PDF with
form fields. LibreOffice stores a normal appearance for every field, which the appearance tests compare with the generated one.

Needs `soffice` and a Python that can `import uno` (run with /usr/bin/python3 where LibreOffice's bindings are installed).
Set SOFFICE to override the binary."""
import os, shutil, subprocess, sys, tempfile, time
import uno
from com.sun.star.beans import PropertyValue
from com.sun.star.awt import Size, Point

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "testdata", "generated")


def pv(name, value):
    p = PropertyValue()
    p.Name, p.Value = name, value
    return p


def main():
    soffice = os.environ.get("SOFFICE") or shutil.which("soffice")
    work = tempfile.mkdtemp(prefix="lo-form-", dir=os.environ.get("TMPDIR"))
    proc = subprocess.Popen([soffice, f"-env:UserInstallation=file://{work}/profile", "--headless", "--norestore",
                             "--accept=socket,host=127.0.0.1,port=2083;urp;"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        ctx = None
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
        for _ in range(60):
            try:
                ctx = resolver.resolve("uno:socket,host=127.0.0.1,port=2083;urp;StarOffice.ComponentContext")
                break
            except Exception:
                time.sleep(1)
        if ctx is None:
            raise RuntimeError("soffice did not start")
        desktop = ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        doc = desktop.loadComponentFromURL("private:factory/swriter", "_blank", 0, (pv("Hidden", True),))
        page = doc.DrawPage
        form = doc.createInstance("com.sun.star.form.component.Form")
        page.Forms.insertByName("Form", form)

        y = 1500  # 1/100 mm from the top margin

        def add(service, name, w, h, **props):
            nonlocal y
            model = doc.createInstance(service)
            model.Name = name
            for k, v in props.items():
                setattr(model, k, v)
            shape = doc.createInstance("com.sun.star.drawing.ControlShape")
            shape.Size = Size(w, h)
            shape.Position = Point(2000, y)
            shape.AnchorType = uno.Enum("com.sun.star.text.TextContentAnchorType", "AT_PAGE")
            shape.Control = model
            form.insertByIndex(form.getCount(), model)
            page.add(shape)
            y += h + 500

        add("com.sun.star.form.component.TextField", "name", 7000, 800, Text="Jane Q. Public", DefaultText="Jane Q. Public", FontHeight=12.0)
        add("com.sun.star.form.component.TextField", "city", 5000, 800, Text="Springfield", DefaultText="Springfield", Align=2, FontHeight=11.0)
        add("com.sun.star.form.component.TextField", "notes", 7000, 2500, Text="A longer note that wraps onto several lines inside the box.", DefaultText="A longer note that wraps onto several lines inside the box.", MultiLine=True, FontHeight=11.0)
        add("com.sun.star.form.component.ComboBox", "fruit", 5000, 800, StringItemList=("Apple", "Banana", "Cherry"), Text="Banana", DefaultText="Banana", Dropdown=True, FontHeight=12.0)
        add("com.sun.star.form.component.ListBox", "colour", 5000, 2200, StringItemList=("Red", "Green", "Blue", "Cyan", "Magenta"), SelectedItems=(1,), DefaultSelection=(1,), Dropdown=False, FontHeight=11.0)
        add("com.sun.star.form.component.CheckBox", "agree", 3500, 700, Label="Agree", State=1, DefaultState=1)
        add("com.sun.star.form.component.CheckBox", "news", 3500, 700, Label="News", State=0)
        add("com.sun.star.form.component.RadioButton", "size", 3500, 700, Label="Small", RefValue="s", State=1, DefaultState=1)
        add("com.sun.star.form.component.RadioButton", "size", 3500, 700, Label="Large", RefValue="l", State=0)
        add("com.sun.star.form.component.CommandButton", "submit", 3500, 900, Label="Submit", FontHeight=12.0)

        out = os.path.join(ROOT, "pdf", "lo-form-fields.pdf")
        fd = uno.Any("[]com.sun.star.beans.PropertyValue", (pv("ExportFormFields", True), pv("UseTaggedPDF", False)))
        uno.invoke(doc, "storeToURL", (uno.systemPathToFileUrl(os.path.abspath(out)),
                                       uno.Any("[]com.sun.star.beans.PropertyValue", (pv("FilterName", "writer_pdf_Export"), pv("FilterData", fd)))))
        doc.close(True)
        try:
            desktop.terminate()
        except Exception:
            pass
    finally:
        proc.terminate()
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
