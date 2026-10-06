# DrawingFactory

Windows/x64 SOLIDWORKS rajzgenerátor és referencia-összehasonlító, .NET 10 SDK-val.

A GitHub-repó a forráskódot, teszteket és dokumentációt tartalmazza. A céges CAD-minták, `Sheetformats` lapformátumok, generált rajzok, tanult `DrawingStyle.json`, naplók és helyi IDE-beállítások szándékosan nincsenek verziókezelve. Ezeket használat előtt helyben kell biztosítani; a stílusprofil a helyi mintákból újratanítható.

## Build és tesztek

```powershell
dotnet build .\DrawingFactory.sln
dotnet test .\tests\DrawingFactory.Tests\DrawingFactory.Tests.csproj
```

A SOLIDWORKS interop DLL-ek helyét a `SolidWorks.Interop.props` tartalmazza. Eltérő telepítési hely:

```powershell
dotnet build .\DrawingFactory.sln -p:SolidWorksApiDirectory="D:\SOLIDWORKS\api\redist"
```

A kódtesztek nem indítják el a SOLIDWORKS alkalmazást. A tényleges tanítás és rajzgenerálás telepített, használható SOLIDWORKS példányt igényel.

## Stílus tanítása

Minden mintamappa tartalmazzon `part.SLDPRT` és `reference.SLDDRW` fájlt:

```text
TrainingSamples\
  sample-01\
    part.SLDPRT
    reference.SLDDRW
```

```powershell
.\bin\Debug\net10.0-windows\DrawingFactory.exe --train .\TrainingSamples .\DrawingStyle.json
```

Argumentumok nélkül a `--train` a munkamappa `TrainingSamples` könyvtárát és `DrawingStyle.json` kimenetét használja. A minták megnyitása csak olvasható módban történik; generált rajz nem szükséges. Hiányos minták vagy elemzési hiba esetén a tanítás sikertelen, és a korábbi profil megmarad. Sikeres újratanítás atomikusan felülírja a megadott JSON-profilt.

A profil a referenciaállományokat, időbélyeget, megfigyelt stílusadatokat és a leggyakoribb támogatott fekvő lapméretet, lapformátumot, valamint a hozzá tartozó pontos léptékeket menti. Ez mintákból származtatott szabályalapú stílustanulás, nem neurális modell.

## Generálás tanult stílussal

```powershell
.\bin\Debug\net10.0-windows\DrawingFactory.exe .\part.SLDPRT .\drawing.DRWDOT --style .\DrawingStyle.json
```

A generátor automatikusan betölti a munkamappában, majd az alkalmazás mellett keresett `DrawingStyle.json` fájlt is. Build és publish során a projektben mentett profil a kimenetbe is másolódik. Profil nélkül a korábbi alapértelmezett szabályok érvényesek. A `--no-style` kapcsolóval a tanult profil figyelmen kívül hagyható.

A tanult lapméretet és léptékeket ténylegesen felhasználja a nézetek és méretsávok elrendezéséhez. Ha a leggyakoribb lépték nem fér el, más megfigyelt léptéket próbál; végül az eredeti léptékhez és szükség esetén nagyobb laphoz tér vissza. Az eredeti alkatrész-orientáció megmarad. A megtanult lapformátumnak a `Sheetformats` katalógusban rendelkezésre kell állnia.

A generálás jelenleg csak a `FlatPart` stratégiát támogatja. A tanult ordinátás méretezés, natív furatfeliratok, menet- és felületjelek automatikus alkalmazása **még nincs megvalósítva**; ezek megfigyelésként szerepelnek a profilban. Meglévő kimeneti rajzot a program nem ír felül.

## Többnézetes próbarajzok a TrainingSamples alkatrészeiből

```powershell
.\bin\Debug\net10.0-windows\DrawingFactory.exe --preview .\TrainingSamples .\TestOutput
```

Ez a külön próbarajz-mód a nem lapos alkatrészeket is kezeli. Minden mintához alkatrészmásolatot és három nézetes SLDDRW-fájlt készít egy új `TestOutput\TrainingSamples_<időbélyeg>_<azonosító>` mappában. Az eredeti mintákat nem módosítja. Elsőszögű elrendezésben elöl-, felül- és jobb oldali nézetet készít, a tanult lapformátummal és elférő léptékkel. Mentés előtt és újranyitás után ellenőrzi a nézeteket és azok alkatrész-hivatkozását.

**Ezek nem teljes gyártási rajzok:** automatikus gyártási méretezés nincs; a rajzlapon PREVIEW felirat szerepel. A futás eredményeit a `preview-results.json` tartalmazza. A sablon alapértelmezésben a SOLIDWORKS beállításaiból jön; harmadik argumentumként explicit DRWDOT is megadható. Hiányos minta vagy sikertelen próbarajz esetén a kilépési kód `1`.

## Összehasonlítás

```powershell
.\bin\Debug\net10.0-windows\DrawingFactory.exe --regression .\TrainingSamples .\Generated
.\bin\Debug\net10.0-windows\DrawingFactory.exe --compare .\part.SLDPRT .\reference.SLDDRW .\generated.SLDDRW
```

Regresszióhoz minden mintához `Generated\<mintanév>\generated.SLDDRW` szükséges. Nulla összehasonlítás nem számít sikeres futásnak.

Kilépési kódok: `0` siker, `1` működési hiba vagy eltérés, `2` hibás parancssor, `4` nem támogatott generálási stratégia.
