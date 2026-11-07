"""Small synthetic Office packages; never use real documents in fixtures."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
from xml.sax.saxutils import escape
import sys

directory = Path(sys.argv[1]); directory.mkdir(parents=True, exist_ok=True)
ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
with ZipFile(directory / 'Synthetic workbook.xlsx', 'w', ZIP_DEFLATED) as package:
    package.writestr('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>')
    package.writestr('_rels/.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="main" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
    package.writestr('xl/workbook.xml', f'<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Statements" sheetId="1" r:id="s1"/><sheet name="Notes" sheetId="2" r:id="s2"/></sheets></workbook>')
    package.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="s2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/></Relationships>')
    rows = ['<row r="1"><c r="A1" t="inlineStr"><is><t>Institution</t></is></c><c r="B1" t="inlineStr"><is><t>Balance</t></is></c></row>', '<row r="2"><c r="A2" t="inlineStr"><is><t>Synthetic Cedar Bank</t></is></c><c r="B2"><v>42.50</v></c><c r="C2"><f>SUM(B2)</f><v>42.50</v></c></row>']
    for number in range(3, 251):
        value = 'BeyondPreviewToken' if number == 250 else 'Synthetic row ' + str(number)
        rows.append(f'<row r="{number}"><c r="A{number}" t="inlineStr"><is><t>{value}</t></is></c></row>')
    package.writestr('xl/worksheets/sheet1.xml', f'<worksheet xmlns="{ns}"><sheetData>{"".join(rows)}</sheetData></worksheet>')
    value = escape('<script>window.sheetMustNotRun=true</script>')
    package.writestr('xl/worksheets/sheet2.xml', f'<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>{value}</t></is></c></row></sheetData></worksheet>')
word = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
with ZipFile(directory / 'Synthetic policy.docx', 'w', ZIP_DEFLATED) as package:
    package.writestr('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
    package.writestr('word/document.xml', f'<w:document xmlns:w="{word}"><w:body><w:p><w:r><w:t>Synthetic training policy</w:t></w:r></w:p><w:p><w:r><w:t>Emergency exits must stay clear. Café supplies are stored separately.</w:t></w:r></w:p></w:body></w:document>')
    package.writestr('word/header1.xml', f'<w:hdr xmlns:w="{word}"><w:p><w:r><w:t>Synthetic safety team</w:t></w:r></w:p></w:hdr>')
    package.writestr('word/footer1.xml', f'<w:ftr xmlns:w="{word}"><w:p><w:r><w:t>Revision five</w:t></w:r></w:p></w:ftr>')
(directory / 'Broken workbook.xlsx').write_text('Synthetic invalid workbook bytes.', encoding='utf-8')
(directory / 'Legacy workbook.xls').write_text('Synthetic unsupported legacy workbook.', encoding='utf-8')
