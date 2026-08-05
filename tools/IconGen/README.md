# IconGen

Renders `src/GlassCoder.Wpf/Assets/glasscoder.ico`. The icon is committed, so this only needs
running when the mark changes.

```
dotnet run --project tools/IconGen -- src/GlassCoder.Wpf/Assets/glasscoder.ico preview.png
```

The second argument is optional: it writes a contact sheet of every size at true scale on a light
and a dark ground, which is the only honest way to judge whether 16px still reads.

## The mark

A pane of pale glass whose fracture has been filled with gold — kintsugi, the house language of
the kintsunai logo — where the seam takes the shape of a terminal prompt, `>_`.

Glass for what the app is for: the loop is meant to be visible, and a seam you can see is the
feature rather than the defect. Gold in the seam for the house. The prompt for what it does, and
because without it a bare chevron is read as an arrow — which is not a guess, it is what the first
three passes actually looked like.

## Why it draws each size instead of scaling one

A single 256px rendering downsampled to 16 is a grey smudge with a gold smear in it. Every size
here is rendered from the same unit-square geometry with its own weights and its own detail
budget: the gloss stops below 24px, the keyline below 32, the molten highlight below 96, and the
vein taper flattens toward uniform as the icon shrinks so the tips do not thin out to nothing.

## What was tried and rejected

Recorded because each one looked reasonable until it was rendered, and the next person to open
this file will otherwise try them again.

- **A solid filled chevron.** Reads as a paper dart. With branch veins crossing the arms it
  acquires wings and becomes a generic media-player logo.
- **A strong taper, roughly 8:1 along the vein.** The chevron becomes a swoosh and the underscore
  collapses into a blob. A vein that varies as much as a real fracture stops being a glyph.
- **Fracture branches, twice.** Struck off at their own angle they read as a pin pushed through
  the mark. Continuing the arms instead, they end at nearly the same x, the eye joins them into a
  vertical bar, and the mark reads `|>`.
- **Plate boundaries in the corners.** Read as a dog-eared page, not as ceramic.

At icon scale the fracture has to be carried by the seam itself. The kintsugi is in the palette,
the gradient and the keyline, not in added detail.
