#!/usr/bin/env python3
"""
build_game_data.py - Comprehensive DDO reference database builder.

Produces ddo_game_data.json: a single structured companion file covering
everything the DDO_Character_Viewer refers to - feats, classes, races,
skills/abilities/saves, all enhancement/destiny/reaper trees (with node
descriptions and effects), augments, filigree, set bonuses, bonus-type
stacking rules, and an item index.

Superset of build_catalog.py (which only emitted augments/filigree/sets/
bonusTypes). The viewer can load this alongside the existing catalog.

Structure source : DDOBuilderV2 DataFiles (github.com/Maetrim/DDOBuilder, GPLv2)
Authoritative text: cross-reference ddo_game_strings.json, built from the
                    game's own client_local_English.dat (see report).

Env overrides:  DDOB_DATA_DIR (DataFiles path), GAMEDATA_OUT (output path)
"""
from __future__ import annotations
import json, os, re, sys
import xml.etree.ElementTree as ET
from pathlib import Path

DATA_DIR = Path(os.environ.get('DDOB_DATA_DIR',
    '/sessions/funny-charming-gauss/mnt/local_9f0aaaf5-4875-49ae-838d-5e88b61717cf--outputs/'
    '.ddobuilder_cache/DDOBuilderV2_2.0.0.79/DataFiles'))
OUT_FILE = Path(os.environ.get('GAMEDATA_OUT',
    str(Path(__file__).resolve().parent / 'ddo_game_data.json')))

ABILITIES = ['Strength','Dexterity','Constitution','Intelligence','Wisdom','Charisma']
SKILLS = {'Balance':'Dexterity','Bluff':'Charisma','Concentration':'Constitution',
    'Diplomacy':'Charisma','Disable Device':'Intelligence','Haggle':'Charisma',
    'Heal':'Wisdom','Hide':'Dexterity','Intimidate':'Charisma','Jump':'Strength',
    'Listen':'Wisdom','Move Silently':'Dexterity','Open Lock':'Dexterity',
    'Perform':'Charisma','Repair':'Intelligence','Search':'Intelligence',
    'Spellcraft':'Intelligence','Spot':'Wisdom','Swim':'Strength','Tumble':'Dexterity',
    'Use Magic Device':'Charisma'}
SAVES = {'Fortitude':'Constitution','Reflex':'Dexterity','Will':'Wisdom'}

def txt(el,tag):
    v=el.findtext(tag); return v.strip() if v and v.strip() else None
def to_num(s):
    if s is None: return None
    try:
        v=float(s); return int(v) if v==int(v) else v
    except (ValueError,TypeError): return s

def parse_effect(eff):
    out={}
    types=[t.text.strip() for t in eff.findall('Type') if t.text and t.text.strip()]
    if types: out['types']=types
    for src,dst in (('Bonus','bonus'),('AType','atype'),('Item','item'),('Value','value'),('DisplayName','displayName')):
        v=txt(eff,src)
        if v: out[dst]=v
    amt=eff.find('Amount')
    if amt is not None and amt.text: out['amount']=to_num(amt.text.strip())
    if eff.find('Rare') is not None: out['rare']=True
    dice=eff.find('Dice')
    if dice is not None:
        dd={}
        for k,tg in (('number','Number'),('sides','Sides')):
            v=txt(dice,tg)
            if v is not None: dd[k]=to_num(v)
        dmg=txt(dice,'Damage')
        if dmg: dd['damage']=dmg
        if dd: out['dice']=dd
    return out

def parse_requirements(rr):
    if rr is None: return None
    def one(r):
        t=txt(r,'Type') or '?'; it=txt(r,'Item') or ''; val=txt(r,'Value')
        s=t+(':'+it if it else '')
        if val: s+=f'({val})'
        return s
    out={}
    direct=[one(r) for r in rr.findall('Requirement')]
    if direct: out['all']=direct
    for grp in rr.findall('RequiresOneOf'):
        out.setdefault('oneOf',[]).append([one(r) for r in grp.findall('Requirement')])
    for grp in rr.findall('RequiresAllOf'):
        out.setdefault('all',[]).extend(one(r) for r in grp.findall('Requirement'))
    none=[]
    for grp in rr.findall('RequiresNoneOf'):
        none.extend(one(r) for r in grp.findall('Requirement'))
    if none: out['noneOf']=none
    return out or None

def build_feats(dd):
    feats={}; p=dd/'Feats.xml'
    if not p.exists(): return feats
    for f in ET.parse(p).getroot().findall('Feat'):
        name=txt(f,'Name')
        if not name: continue
        e={}
        for src,dst in (('Description','description'),('Acquire','acquire'),('Icon','icon')):
            v=txt(f,src)
            if v: e[dst]=v
        groups=[g.text.strip() for g in f.findall('Group') if g.text and g.text.strip()]
        if groups: e['groups']=groups
        reqs=parse_requirements(f.find('Requirements'))
        if reqs: e['requirements']=reqs
        eff=[parse_effect(x) for x in f.findall('Effect')]
        if eff: e['effects']=eff
        feats[name]=e
    return feats

def build_classes(dd):
    classes={}; cdir=dd/'Classes'
    if not cdir.exists(): return classes
    for path in sorted(cdir.glob('*.class.xml')):
        try: root=ET.parse(path).getroot()
        except ET.ParseError: continue
        for c in root.findall('Class'):
            name=txt(c,'Name')
            if not name: continue
            e={}
            for src,dst in (('BaseClass','baseClass'),('Description','description'),('LargeIcon','icon'),('CastingStat','castingStat')):
                v=txt(c,src)
                if v: e[dst]=v
            hp=txt(c,'HitPoints')
            if hp: e['hitPoints']=to_num(hp)
            sp=txt(c,'SkillPoints')
            if sp: e['skillPoints']=to_num(sp)
            cs=[s.text.strip() for s in c.findall('ClassSkill') if s.text and s.text.strip()]
            if cs: e['classSkills']=cs
            al=[a.text.strip() for a in c.findall('Alignment') if a.text and a.text.strip()]
            if al: e['alignments']=al
            saves={}
            for s in ('Fortitude','Reflex','Will'):
                v=txt(c,s)
                if v: saves[s]=v
            if saves: e['saveTypes']=saves
            spp=txt(c,'SpellPointsPerLevel')
            if spp: e['spellPointsPerLevel']=[to_num(x) for x in spp.split()]
            ft=[t.text.strip() for t in c.findall('ClassSpecificFeatType') if t.text and t.text.strip()]
            if ft: e['classSpecificFeatTypes']=ft
            classes[name]=e
    return classes

def classify_tree(name,icon,bg):
    bgl=(bg or '').lower(); icl=(icon or '').lower()
    if 'reaper' in icl or 'reaper' in name.lower(): return 'reaper'
    if bgl.startswith('destiny') or bgl=='epicdestiny': return 'destiny'
    if bgl=='universal': return 'universal'
    if bgl.endswith('background') and not any(bgl==c.lower()+'background' for c in CLASS_BGS): return 'racial'
    return 'class'

CLASS_BGS={'Artificer','Barbarian','Bard','Cleric','Druid','Fighter','Monk','Paladin',
    'Ranger','Rogue','Sorcerer','Warlock','Wizard','FavoredSoul','Favored Soul','Alchemist'}

def build_trees(dd):
    trees={}; tdir=dd/'EnhancementTrees'
    if not tdir.exists(): return trees
    for path in sorted(tdir.glob('*.tree.xml')):
        try: root=ET.parse(path).getroot()
        except ET.ParseError: continue
        for t in root.findall('EnhancementTree'):
            tn=txt(t,'Name')
            if not tn: continue
            icon=txt(t,'Icon'); bg=txt(t,'Background')
            items=[]
            cont=t.find('Enhancements')
            it_iter=cont.findall('EnhancementTreeItem') if cont is not None else t.findall('EnhancementTreeItem')
            for it in it_iter:
                node={}
                for src,dst in (('Name','name'),('InternalName','internal'),('Description','description'),('Icon','icon')):
                    v=txt(it,src)
                    if v: node[dst]=v
                for src,dst in (('XPosition','x'),('YPosition','y'),('Ranks','ranks'),('MinSpent','minSpent')):
                    v=txt(it,src)
                    if v is not None: node[dst]=to_num(v)
                cpr=txt(it,'CostPerRank')
                if cpr: node['costPerRank']=[to_num(x) for x in cpr.split()]
                reqs=parse_requirements(it.find('Requirements'))
                if reqs: node['requirements']=reqs
                eff=[parse_effect(x) for x in it.findall('Effect')]
                if eff: node['effects']=eff
                sel=it.find('Selector')
                if sel is not None:
                    ch=[]
                    for es in sel.findall('EnhancementSelection'):
                        c={}
                        for src,dst in (('Name','name'),('Description','description'),('Icon','icon')):
                            v=txt(es,src)
                            if v: c[dst]=v
                        ce=[parse_effect(x) for x in es.findall('Effect')]
                        if ce: c['effects']=ce
                        if c: ch.append(c)
                    if ch: node['selector']=ch
                if node: items.append(node)
            trees[tn]={'kind':classify_tree(tn,icon,bg),'icon':icon,'background':bg,'items':items}
    return trees

def build_races(dd,feats,trees):
    feat_races=set()
    for f in feats.values():
        rq=f.get('requirements') or {}
        for r in rq.get('all',[]):
            if r.startswith('Race:'): feat_races.add(r.split(':',1)[1])
        for grp in rq.get('oneOf',[]):
            for r in grp:
                if r.startswith('Race:'): feat_races.add(r.split(':',1)[1])
    ARCH=re.compile(r'(archer|chaos|trailblazer|scoundrel|chaosmancer|mage|knight|hunter|servant|disciple)',re.I)
    tree_races=set()
    for tn,tv in trees.items():
        if tv.get('kind')!='racial': continue
        base=tn.split(':')[0].strip()
        if '(' in base or ARCH.search(base): continue
        tree_races.add(base)
    races={}
    for r in sorted(feat_races|tree_races):
        linked=[tn for tn,tv in trees.items() if tv.get('kind')=='racial' and
                (tn.split(':')[0].strip().lower()==r.lower() or tn.lower().startswith(r.lower()))]
        races[r]={'trees':linked} if linked else {}
    return races

def build_filigree(dd):
    filigree={}; sets={}; fdir=dd/'FiligreeSets'
    if not fdir.exists(): return filigree,sets
    for path in sorted(fdir.glob('*.Filigree.xml')):
        try: root=ET.parse(path).getroot()
        except ET.ParseError: continue
        for sb in root.findall('SetBonus'):
            sn=txt(sb,'Type')
            if not sn: continue
            tiers=[]
            for b in sb.findall('Buff'):
                pc=txt(b,'EquippedCount')
                if pc is None: continue
                tiers.append({'pieces':to_num(pc),'description':txt(b,'Description') or '','effects':[parse_effect(e) for e in b.findall('Effect')]})
            if tiers: sets[sn]={'kind':'filigree','tiers':tiers}
        for fil in root.findall('Filigree'):
            nm=txt(fil,'Name')
            if not nm: continue
            filigree[nm]={'set':txt(fil,'SetBonus'),'description':txt(fil,'Description') or '','effects':[parse_effect(e) for e in fil.findall('Effect')]}
    return filigree,sets

def build_augments(dd):
    aug={}; adir=dd/'Augments'
    if not adir.exists(): return aug
    for path in sorted(adir.glob('*.Augments.xml')):
        try: root=ET.parse(path).getroot()
        except ET.ParseError: continue
        for a in root.findall('Augment'):
            nm=txt(a,'Name')
            if not nm: continue
            aug[nm]={'slotType':txt(a,'Type'),'minLevel':to_num(txt(a,'MinLevel')),'description':txt(a,'Description') or '','effects':[parse_effect(e) for e in a.findall('Effect')]}
    return aug

def merge_gear_sets(dd,sets):
    p=dd/'SetBonuses.xml'
    if not p.exists(): return
    for sb in ET.parse(p).getroot().findall('SetBonus'):
        sn=txt(sb,'Type')
        if not sn: continue
        tiers=[]
        for b in sb.findall('Buff'):
            pc=txt(b,'EquippedCount')
            if pc is None: continue
            tiers.append({'pieces':to_num(pc),'description':txt(b,'Description') or '','effects':[parse_effect(e) for e in b.findall('Effect')]})
        if tiers:
            ex=sets.get(sn)
            if ex and len(ex.get('tiers',[]))>=len(tiers): continue
            sets[sn]={'kind':'gear','tiers':tiers}

def build_bonus_types(dd):
    bt={}; p=dd/'BonusTypes.xml'
    if p.exists():
        for b in ET.parse(p).getroot().findall('Bonus'):
            nm=txt(b,'Name'); st=txt(b,'Stacking')
            if nm and st: bt[nm.rstrip()]=st
    return bt

def build_items(dd):
    items={}; idir=dd/'Items'
    if not idir.exists(): return items
    for path in sorted(idir.glob('*.item')):
        try: root=ET.parse(path).getroot()
        except ET.ParseError: continue
        for it in root.findall('Item'):
            nm=txt(it,'Name')
            if not nm: continue
            e={}
            ic=txt(it,'Icon')
            if ic: e['icon']=ic
            ml=txt(it,'MinLevel')
            if ml is not None: e['minLevel']=to_num(ml)
            slot=it.find('EquipmentSlot')
            if slot is not None:
                sl=[c.tag for c in slot]
                if sl: e['slots']=sl
            w=txt(it,'Weapon')
            if w: e['weapon']=w
            ar=txt(it,'Armor')
            if ar: e['armor']=ar
            nb=len(it.findall('Buff'))
            if nb: e['buffCount']=nb
            items[nm]=e
    return items

def main():
    if not DATA_DIR.exists():
        print(f'ERR: DataFiles not found at {DATA_DIR}',file=sys.stderr); return 1
    print(f'Source: {DATA_DIR}')
    feats=build_feats(DATA_DIR); print(f'  feats:    {len(feats)}')
    classes=build_classes(DATA_DIR); print(f'  classes:  {len(classes)}')
    trees=build_trees(DATA_DIR); print(f'  trees:    {len(trees)}')
    races=build_races(DATA_DIR,feats,trees); print(f'  races:    {len(races)}')
    filigree,sets=build_filigree(DATA_DIR)
    augment=build_augments(DATA_DIR); merge_gear_sets(DATA_DIR,sets)
    bonus_types=build_bonus_types(DATA_DIR)
    print(f'  augments: {len(augment)}  filigree: {len(filigree)}  sets: {len(sets)}  bonusTypes: {len(bonus_types)}')
    items=build_items(DATA_DIR); print(f'  items:    {len(items)}')
    kinds={}
    for tv in trees.values(): kinds[tv['kind']]=kinds.get(tv['kind'],0)+1
    db={'meta':{'structureSource':'DDOBuilderV2 2.0.0.79 DataFiles (github.com/Maetrim/DDOBuilder, GPLv2)',
        'textSource':'cross-reference ddo_game_strings.json (client_local_English.dat)',
        'counts':{'feats':len(feats),'classes':len(classes),'races':len(races),'skills':len(SKILLS),
            'trees':len(trees),'treeKinds':kinds,'augments':len(augment),'filigree':len(filigree),
            'setBonuses':len(sets),'bonusTypes':len(bonus_types),'items':len(items)}},
        'abilities':ABILITIES,'skills':SKILLS,'saves':SAVES,'feats':feats,'classes':classes,
        'races':races,'trees':trees,'augment':augment,'filigree':filigree,'setBonuses':sets,
        'bonusTypes':bonus_types,'items':items}
    OUT_FILE.parent.mkdir(parents=True,exist_ok=True)
    OUT_FILE.write_text(json.dumps(db,ensure_ascii=False,separators=(',',':')))
    print(f'\nWrote: {OUT_FILE}\nSize:  {OUT_FILE.stat().st_size/1024:.1f} KB')
    return 0

if __name__=='__main__':
    sys.exit(main())
