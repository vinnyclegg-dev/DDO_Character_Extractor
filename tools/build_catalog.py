#!/usr/bin/env python3
"""
Walk DDOBuilder's DataFiles folder and emit a compact JSON catalog
containing only the fields the DDO_Character_Viewer cares about:
  - filigree[name]:       per-filigree effect list (with Rare variants)
  - augment[name]:        per-augment effect list
  - setBonuses[setName]:  tiered effects keyed by piece-count threshold
  - bonusTypes[name]:     stacking rule ("Highest Only" / "Always")
  - filigreeByNorm[norm]: lookup table — strips "+N" so user-format names
                          like "Wildhunter: Dexterity (Rare)" can match the
                          DDOBuilder catalog name "Wildhunter: +1 Dexterity"

Data source: https://github.com/Maetrim/DDOBuilder (GPLv2, data files only).
"""
from __future__ import annotations

import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# DataFiles + output locations are overridable via env so the ddobuilder-sync
# skill can rebuild against an arbitrary DDOBuilder release without editing
# this file. Defaults point at the user's current local install + project
# catalog path.
DATA_DIR = Path(os.environ.get(
    'DDOB_DATA_DIR',
    '/sessions/modest-lucid-wright/mnt/Vinny/Downloads/DDOBuilderV2_2.0.0.79/DataFiles',
))
OUT_FILE = Path(os.environ.get(
    'DDOB_OUT_FILE',
    str(Path(__file__).resolve().parent / 'ddo_catalog.json'),
))


def parse_effect(eff: ET.Element) -> dict:
    """Convert a single <Effect> node into a compact dict."""
    types = [t.text.strip() for t in eff.findall('Type') if t.text]
    bonus = (eff.findtext('Bonus') or '').strip() or None
    atype = (eff.findtext('AType') or '').strip() or None
    amount_el = eff.find('Amount')
    amount = None
    if amount_el is not None and amount_el.text:
        try:
            v = float(amount_el.text)
            amount = int(v) if v == int(v) else v
        except ValueError:
            amount = amount_el.text.strip()
    item = (eff.findtext('Item') or '').strip() or None
    value_text = (eff.findtext('Value') or '').strip() or None
    display_name = (eff.findtext('DisplayName') or '').strip() or None
    is_rare = eff.find('Rare') is not None
    dice = None
    dice_el = eff.find('Dice')
    if dice_el is not None:
        try:
            dice = {
                'number': int(dice_el.findtext('Number') or 0),
                'sides': int(dice_el.findtext('Sides') or 0),
                'damage': (dice_el.findtext('Damage') or '').strip() or None,
            }
        except (ValueError, TypeError):
            pass
    result = {'types': types}
    if bonus:        result['bonus']       = bonus
    if atype:        result['atype']       = atype
    if amount is not None: result['amount'] = amount
    if item:         result['item']        = item
    if value_text:   result['value']       = value_text
    if display_name: result['displayName'] = display_name
    if is_rare:      result['rare']        = True
    if dice:         result['dice']        = dice
    return result


def parse_filigree_file(path: Path, out_filigrees: dict, out_set_bonuses: dict) -> None:
    tree = ET.parse(path)
    root = tree.getroot()
    # Tier definitions live in <SetBonus> at the file root
    for sb in root.findall('SetBonus'):
        set_name = (sb.findtext('Type') or '').strip()
        if not set_name:
            continue
        tiers = []
        for buff in sb.findall('Buff'):
            try:
                pieces = int(buff.findtext('EquippedCount') or 0)
            except (ValueError, TypeError):
                continue
            desc = (buff.findtext('Description') or '').strip()
            effects = [parse_effect(e) for e in buff.findall('Effect')]
            tiers.append({'pieces': pieces, 'description': desc, 'effects': effects})
        if tiers:
            out_set_bonuses[set_name] = {'kind': 'filigree', 'tiers': tiers}
    # Individual filigrees
    for fil in root.findall('Filigree'):
        name = (fil.findtext('Name') or '').strip()
        if not name:
            continue
        set_name = (fil.findtext('SetBonus') or '').strip() or None
        effects = [parse_effect(e) for e in fil.findall('Effect')]
        out_filigrees[name] = {
            'set': set_name,
            'description': (fil.findtext('Description') or '').strip(),
            'effects': effects,
        }


def parse_augment_file(path: Path, out_augments: dict) -> None:
    tree = ET.parse(path)
    root = tree.getroot()
    for aug in root.findall('Augment'):
        name = (aug.findtext('Name') or '').strip()
        if not name:
            continue
        slot_type = (aug.findtext('Type') or '').strip() or None
        min_level = (aug.findtext('MinLevel') or '').strip() or None
        try:
            min_level_int = int(min_level) if min_level else None
        except ValueError:
            min_level_int = None
        effects = [parse_effect(e) for e in aug.findall('Effect')]
        out_augments[name] = {
            'slotType': slot_type,
            'minLevel': min_level_int,
            'description': (aug.findtext('Description') or '').strip(),
            'effects': effects,
        }


def parse_set_bonuses_file(path: Path, out_set_bonuses: dict) -> None:
    """SetBonuses.xml carries the gear-item set tiers (Lamordian, etc.)."""
    tree = ET.parse(path)
    root = tree.getroot()
    for sb in root.findall('SetBonus'):
        set_name = (sb.findtext('Type') or '').strip()
        if not set_name:
            continue
        tiers = []
        for buff in sb.findall('Buff'):
            try:
                pieces = int(buff.findtext('EquippedCount') or 0)
            except (ValueError, TypeError):
                continue
            desc = (buff.findtext('Description') or '').strip()
            effects = [parse_effect(e) for e in buff.findall('Effect')]
            tiers.append({'pieces': pieces, 'description': desc, 'effects': effects})
        if tiers:
            # If the same name was already added from a filigree file, prefer
            # whichever has more tiers (most complete)
            existing = out_set_bonuses.get(set_name)
            if existing and len(existing.get('tiers', [])) >= len(tiers):
                continue
            out_set_bonuses[set_name] = {'kind': 'gear', 'tiers': tiers}


def parse_bonus_types_file(path: Path, out_bonus_types: dict) -> None:
    tree = ET.parse(path)
    root = tree.getroot()
    for b in root.findall('Bonus'):
        name = (b.findtext('Name') or '').strip()
        stack = (b.findtext('Stacking') or '').strip()
        if name and stack:
            # Strip trailing spaces that exist in the data ("Competence ")
            out_bonus_types[name.rstrip()] = stack


def normalize_filigree_name(name: str) -> str:
    """Strip the "+N " value and "(Rare)" tag from a filigree name so
    user-format names ("Wildhunter: Dexterity (Rare)") match the catalog
    names ("Wildhunter: +1 Dexterity")."""
    n = name.strip()
    n = re.sub(r'\s*\(Rare\)\s*$', '', n, flags=re.IGNORECASE)
    # Strip "+<number><optional unit>" after the colon — handles "+1", "+10", "+3%"
    n = re.sub(r':\s*\+\d+\s+', ': ', n)
    # Also handle filigrees without the "+N" prefix (just in case)
    return re.sub(r'\s+', ' ', n).lower().strip()


def normalize_augment_name(name: str) -> str:
    """Map disparate augment naming conventions to a common form so the engine's
    "Melancholic: Dexterity" matches DDOBuilder's "Melancholic Dexterity
    (Legendary)", and "Diamond of Festive Dexterity +2" matches "+2 Festive
    Dexterity". The normalized key drops tier suffixes, the "Diamond of"
    prefix, colons, and moves trailing "+N" magnitudes to the front."""
    n = name.lower().strip()
    # Strip "Diamond of " prefix
    n = re.sub(r'^diamond of\s+', '', n)
    # Strip tier suffix
    n = re.sub(r'\s*\((legendary|heroic|epic|mythic|raid)\)\s*$', '', n)
    # Strip colons (Lamordia "Melancholic: Dexterity" → "Melancholic Dexterity")
    n = re.sub(r':\s*', ' ', n)
    # If name ends in "+N", move it to the front
    m = re.match(r'^(.+?)\s+\+(\d+)$', n)
    if m:
        n = f'+{m.group(2)} {m.group(1)}'
    return re.sub(r'\s+', ' ', n).strip()


def main() -> int:
    if not DATA_DIR.exists():
        print(f'ERR: DataFiles not found at {DATA_DIR}', file=sys.stderr)
        return 1

    filigree: dict = {}
    augment: dict = {}
    set_bonuses: dict = {}
    bonus_types: dict = {}

    # Filigree files (also seed set_bonuses with filigree-set tiers)
    fil_dir = DATA_DIR / 'FiligreeSets'
    n_fil_files = 0
    for path in sorted(fil_dir.glob('*.Filigree.xml')):
        try:
            parse_filigree_file(path, filigree, set_bonuses)
            n_fil_files += 1
        except Exception as ex:  # noqa: BLE001
            print(f'WARN: filigree {path.name}: {ex}', file=sys.stderr)
    print(f'Filigree files parsed: {n_fil_files}')
    print(f'Filigrees indexed: {len(filigree)}')

    # Augment files
    aug_dir = DATA_DIR / 'Augments'
    n_aug_files = 0
    for path in sorted(aug_dir.glob('*.Augments.xml')):
        try:
            parse_augment_file(path, augment)
            n_aug_files += 1
        except Exception as ex:  # noqa: BLE001
            print(f'WARN: augment {path.name}: {ex}', file=sys.stderr)
    print(f'Augment files parsed: {n_aug_files}')
    print(f'Augments indexed: {len(augment)}')

    # Gear set bonuses (top-level SetBonuses.xml)
    sb_file = DATA_DIR / 'SetBonuses.xml'
    if sb_file.exists():
        parse_set_bonuses_file(sb_file, set_bonuses)
    print(f'Set bonuses indexed (gear + filigree): {len(set_bonuses)}')

    # Bonus types
    bt_file = DATA_DIR / 'BonusTypes.xml'
    if bt_file.exists():
        parse_bonus_types_file(bt_file, bonus_types)
    print(f'Bonus types indexed: {len(bonus_types)}')

    # Build the normalized-name lookup for filigrees
    filigree_by_norm: dict = {}
    for name in filigree:
        filigree_by_norm.setdefault(normalize_filigree_name(name), name)

    # Build the normalized-name lookup for augments. Multiple raw names can
    # map to the same normalized key (e.g. Heroic vs Legendary tier of the
    # same gem), so values are lists; the viewer disambiguates by tier suffix.
    augment_by_norm: dict = {}
    for name in augment:
        key = normalize_augment_name(name)
        augment_by_norm.setdefault(key, []).append(name)

    catalog = {
        'meta': {
            'source': 'DDOBuilder (https://github.com/Maetrim/DDOBuilder)',
            'license': 'GPLv2 (data files used for personal viewer)',
            'filigreeCount': len(filigree),
            'augmentCount': len(augment),
            'setBonusCount': len(set_bonuses),
            'bonusTypeCount': len(bonus_types),
        },
        'filigree': filigree,
        'filigreeByNorm': filigree_by_norm,
        'augment': augment,
        'augmentByNorm': augment_by_norm,
        'setBonuses': set_bonuses,
        'bonusTypes': bonus_types,
    }

    OUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    OUT_FILE.write_text(json.dumps(catalog, ensure_ascii=False, separators=(',', ':')))
    print(f'\nWrote: {OUT_FILE}')
    print(f'Size:  {OUT_FILE.stat().st_size / 1024:.1f} KB')
    return 0


if __name__ == '__main__':
    sys.exit(main())
