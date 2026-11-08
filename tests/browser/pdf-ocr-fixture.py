"""Generate only synthetic OCR PDFs. Production uses Poppler/Tesseract, not Python."""
from pathlib import Path
import sys
from PIL import Image, ImageDraw, ImageFont
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader
from reportlab.lib.pdfencrypt import StandardEncryption
folder=Path(sys.argv[1]);folder.mkdir(parents=True,exist_ok=True)
font=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',42)
def scan(lines,rotate=0):
    image=Image.new('RGB',(1600,2000),'white');draw=ImageDraw.Draw(image)
    for i,line in enumerate(lines):draw.text((90,100+i*115),line,font=font,fill='black')
    return image.rotate(rotate,expand=True) if rotate else image
def page(pdf,image):
    pdf.drawImage(ImageReader(image),0,0,width=612,height=792);pdf.showPage()
statement=scan(['SYNTHETIC CEDAR BANK','MONTHLY ACCOUNT STATEMENT','Account ending 1234','Statement date October 31, 2026','Opening balance 1200.00','Deposits received 2400.00','Purchases and payments 1100.00','Closing balance 2500.00','Keep this statement for your records','Synthetic scanned PDF for OCR testing'])
safety=scan(['SYNTHETIC SAFETY MINISTRY','SECURITY TRAINING POLICY','Revision five effective October 2026','Emergency exits must stay clear','Report every safety incident promptly','Monthly training for all volunteers','Meet at the north entrance each Sunday','Policy confirmation code NORTH7350'])
pdf=canvas.Canvas(str(folder/'Scanned statement.pdf'),pagesize=(612,792));page(pdf,statement);pdf.save()
pdf=canvas.Canvas(str(folder/'Mixed policy.pdf'),pagesize=(612,792));pdf.setFont('Helvetica',18);pdf.drawString(50,700,'Synthetic church safety team training cover sheet');pdf.drawString(50,650,'Keep this cover with the attached scanned policy.');pdf.showPage();page(pdf,safety);pdf.save()
pdf=canvas.Canvas(str(folder/'Rotated scan.pdf'),pagesize=(612,792));page(pdf,scan(['SYNTHETIC CEDAR BANK','MONTHLY ACCOUNT STATEMENT','Account ending 9876','Statement date November 30, 2026','Opening balance 3000.00','Closing balance 3500.00','Keep this statement for your records','Synthetic sideways scanned PDF'],90));pdf.save()
pdf=canvas.Canvas(str(folder/'Footer scan.pdf'),pagesize=(612,792));pdf.drawImage(ImageReader(safety),0,30,width=612,height=750);pdf.setFont('Helvetica',8);pdf.drawString(50,12,'Page 1');pdf.showPage();pdf.save()
pdf=canvas.Canvas(str(folder/'Native text.pdf'),pagesize=(612,792));pdf.setFont('Helvetica',16);pdf.drawString(50,700,'Synthetic digitally authored document with searchable native text.');pdf.drawString(50,650,'NativeTextSentinel confirmation 52790. No OCR is needed.');pdf.showPage();pdf.save()
pdf=canvas.Canvas(str(folder/'Blank pages.pdf'),pagesize=(612,792));pdf.showPage();pdf.showPage();pdf.save()
pdf=canvas.Canvas(str(folder/'Locked scan.pdf'),pagesize=(612,792),encrypt=StandardEncryption('synthetic-pass'));page(pdf,statement);pdf.save()
pdf=canvas.Canvas(str(folder/'Page boundary.pdf'),pagesize=(612,792));pdf.setFont('Helvetica',14);pdf.drawString(50,700,'Synthetic readable first page with enough embedded text.');pdf.showPage();pdf.showPage();page(pdf,safety);pdf.save()
pdf=canvas.Canvas(str(folder/'Long native.pdf'),pagesize=(612,792))
for i in range(101):
    pdf.setFont('Helvetica',14);pdf.drawString(50,700,'Synthetic digitally authored text for PDF page '+str(i+1));pdf.showPage()
pdf.save()
(folder/'Broken.pdf').write_bytes(b'%PDF-1.7\nSynthetic malformed PDF bytes.\n%%EOF')
print('Synthetic PDF OCR fixtures ready.')
