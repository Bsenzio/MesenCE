import json, hashlib, os
rom = r'D:\MesenHighWork\test-roms-new2\hdstudio_test.nes'
sha = hashlib.sha256(open(rom, 'rb').read()).hexdigest()
entry = {
    'name': 'hdstudio_test.nes', 'path': rom, 'relativePath': 'hdstudio_test.nes', 'sha256': sha,
    'title': 'hdstudio_test', 'baseTitle': 'hdstudio_test', 'normalizedTitle': 'hdstudio_test',
    'region': '', 'languages': '', 'revision': '', 'flags': [], 'isMulticart': False,
    'isPirate': False, 'isHomebrew': True, 'isHack': False, 'isUnlicensed': False,
    'headerValid': True, 'nes20': False, 'mapper': 0, 'submapper': 0, 'prgKb': 16,
    'chrKb': 8, 'chrType': 'CHR-ROM', 'prgRamKb': 0, 'chrRamKb': 0, 'battery': False,
    'fourScreen': False, 'mirroring': 'Horizontal', 'consoleType': 'nes',
    'fileBytes': os.path.getsize(rom), 'trailingBytes': 0, 'eepromKb': 0,
    'mapperBand': '0', 'chrSizeBucket': '<=32K', 'prgSizeBucket': '<=32K', 'split': 'development'
}
json.dump({'summary': {'uniqueRoms': 1, 'splits': {'development': 1, 'validation': 0, 'holdout': 0}},
           'roms': [entry], 'duplicates': [], 'invalid': []},
          open(r'D:\MesenHighWork\validation-v071\inventory\synthetic-inventory.json', 'w'), indent=1)
print(sha)
