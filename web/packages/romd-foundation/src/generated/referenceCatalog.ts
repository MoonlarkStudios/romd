// Generated from reference-data/catalog. Do not edit.
export const systemKeys = {
  "Atari2600": "2600",
  "Sega32X": "32x",
  "ThreeDo": "3do",
  "Nintendo3Ds": "3ds",
  "Atari5200": "5200",
  "Atari7800": "7800",
  "Amiga": "amiga",
  "Arcade": "arcade",
  "AtariSt": "atarist",
  "Commodore64": "c64",
  "CdI": "cdi",
  "ColecoVision": "coleco",
  "Dreamcast": "dc",
  "Dos": "dos",
  "GameBoy": "gb",
  "GameBoyAdvance": "gba",
  "GameBoyColor": "gbc",
  "GameCube": "gc",
  "Genesis": "genesis",
  "GameGear": "gg",
  "Intellivision": "intv",
  "Jaguar": "jaguar",
  "Lynx": "lynx",
  "Msx": "msx",
  "N64": "n64",
  "NintendoDs": "nds",
  "NeoGeo": "neogeo",
  "NeoGeoCd": "neogeocd",
  "Nes": "nes",
  "NeoGeoPocket": "ngp",
  "NeoGeoPocketColor": "ngpc",
  "PlayStation2": "ps2",
  "PlayStation3": "ps3",
  "PlayStation4": "ps4",
  "PlayStation5": "ps5",
  "Psp": "psp",
  "PlayStation": "psx",
  "Saturn": "saturn",
  "SegaCd": "segacd",
  "SuperGrafx": "sgx",
  "MasterSystem": "sms",
  "Snes": "snes",
  "Switch": "switch",
  "TurboGrafx16": "tg16",
  "TurboGrafxCd": "tgcd",
  "VirtualBoy": "vb",
  "Vita": "vita",
  "Wii": "wii",
  "WiiU": "wiiu",
  "WonderSwan": "ws",
  "WonderSwanColor": "wsc",
  "Xbox360": "x360",
  "Xbox": "xbox",
  "XboxOne": "xone",
  "XboxSeriesX": "xsx",
  "ZxSpectrum": "zxs"
} as const;
export type KnownSystemKey = typeof systemKeys[keyof typeof systemKeys];
export const ratingBoards = [
  {
    "key": "Esrb",
    "label": "ESRB",
    "value": 0,
    "prefixes": [
      "ESRB"
    ]
  },
  {
    "key": "Pegi",
    "label": "PEGI",
    "value": 1,
    "prefixes": [
      "PEGI"
    ]
  },
  {
    "key": "Cero",
    "label": "CERO",
    "value": 2,
    "prefixes": [
      "CERO"
    ]
  },
  {
    "key": "Usk",
    "label": "USK",
    "value": 3,
    "prefixes": [
      "USK"
    ]
  },
  {
    "key": "Grac",
    "label": "GRAC",
    "value": 4,
    "prefixes": [
      "GRAC"
    ]
  },
  {
    "key": "ClassInd",
    "label": "ClassInd",
    "value": 5,
    "prefixes": [
      "CLASS IND",
      "CLASSIND"
    ]
  },
  {
    "key": "Acb",
    "label": "ACB",
    "value": 6,
    "prefixes": [
      "ACB"
    ]
  }
] as const;
