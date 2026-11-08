"""Synthetic printed pages only. Pillow creates pixels; Magick.NET/Tesseract are the production readers."""
from pathlib import Path
import sys, shutil
from PIL import Image, ImageDraw, ImageFont
folder=Path(sys.argv[1]);folder.mkdir(parents=True,exist_ok=True)
font=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',42)
page=Image.new('RGB',(1600,1400),'white');draw=ImageDraw.Draw(page)
for i,line in enumerate(['SYNTHETIC CEDAR BANK','MONTHLY ACCOUNT STATEMENT','Account ending 1234','Statement date October 31, 2026','Opening balance 1200.00','Deposits received 2400.00','Purchases and payments 1100.00','Closing balance 2500.00','Keep this statement for your records','Synthetic document for local OCR testing']):draw.text((90,80+i*110),line,font=font,fill='black')
for ext in ['png','jpg','bmp','webp','gif','avif']:page.save(folder/('Synthetic statement.'+ext))
second=Image.new('RGB',(1600,1400),'white');draw=ImageDraw.Draw(second)
draw.text((100,100),'SECOND PAGE CONFIRMATION',font=font,fill='black');draw.text((100,250),'Synthetic approved total 7350.25',font=font,fill='black')
page.save(folder/'Synthetic multipage.tiff',save_all=True,append_images=[second],compression='tiff_lzw')
page.rotate(90,expand=True).save(folder/'Synthetic rotated.png')
Image.new('RGB',(800,800),'white').save(folder/'Blank photo.png')
(folder/'Damaged.tiff').write_bytes(b'II*\x00\x08\x00\x00\x00broken')

shutil.copyfile(Path(__file__).parents[1]/'DocumentStorage.Checks/Fixtures/synthetic-statement.heic',folder/'Synthetic phone.heic')
