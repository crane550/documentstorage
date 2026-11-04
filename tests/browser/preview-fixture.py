"""Generate synthetic preview fixtures under ignored .local/. Requires reportlab and Pillow."""
from pathlib import Path
from uuid import uuid4
import json
from reportlab.pdfgen import canvas
from reportlab.lib.pdfencrypt import StandardEncryption
from reportlab.lib import colors
from PIL import Image, ImageDraw

folder = Path('.local', 'preview-browser-' + uuid4().hex).resolve()
folder.mkdir(parents=True)
for filename, password in [('statement.pdf', None), ('locked.pdf', 'fixture-pass')]:
    pdf = canvas.Canvas(str(folder / filename), pagesize=(612, 792),
                        encrypt=StandardEncryption(password) if password else None)
    for page in range(1, 4):
        pdf.setFont('Helvetica', 24)
        pdf.drawString(65, 700, f'Synthetic statement page {page}')
        pdf.setFont('Helvetica', 14)
        pdf.drawString(65, 650, 'Cedar Bank search needle ' + str(page))
        pdf.drawString(65, 620, 'All fixture amounts and names are synthetic.')
        pdf.acroForm.textfield(name=f'field-{page}', value='Synthetic filled form', x=65, y=540,
                              width=300, height=30, textColor=colors.red,
                              fillColor=colors.lightyellow, forceBorder=True)
        pdf.showPage()
    pdf.save()
image = Image.new('RGB', (1200, 800), (232, 220, 198))
ImageDraw.Draw(image).text((100, 100), 'Synthetic image preview', (80, 58, 38))
for name in ['photo.png', 'photo.jpg', 'photo.webp', 'photo.gif', 'photo.bmp', 'photo.avif']:
    image.save(folder / name)
(folder / 'notes.md').write_text('# Synthetic notes\n\nPreview should show <script>window.previewPwned=true</script> as text.\n')
(folder / 'data.csv').write_text('institution,date,amount\nCedar,2026-01-31,42.50\n')
(folder / 'unsupported.docx').write_text('Synthetic unsupported file')
(folder / 'broken.pdf').write_text('%PDF-1.7\nThis is a broken synthetic PDF.')
(folder / 'fake.png').write_text('<html><script>window.previewPwned=true</script></html>')
Path('.local/preview-fixture.json').write_text(json.dumps({'directory': str(folder)}))
print('Synthetic preview fixtures ready in ignored .local/.')
