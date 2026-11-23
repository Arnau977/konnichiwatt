# Konnichiwatt

Batería de tus dispositivos inalámbricos siempre visible en la barra de tareas de Windows 11, junto a la bandeja del sistema.

```
 🎧 28%   🖱 62%   ^  …  ESP  🔊  13:22
```

## Descargar

**[Descargar Konnichiwatt.exe (instalador)](https://github.com/Arnau977/konnichiwatt/releases/latest/download/Konnichiwatt.exe)**
· [versión portable (.zip)](https://github.com/Arnau977/konnichiwatt/releases/latest/download/Konnichiwatt-portable.zip)

**Instalador**: doble clic en el `.exe` descargado → *Instalar*. Se instala solo para tu usuario (sin permisos de
administrador) en `%LOCALAPPDATA%\Programs\Konnichiwatt`, con acceso en el menú Inicio y entrada en
*Configuración › Aplicaciones* para desinstalarlo. Después puedes borrar el archivo descargado. Abrir de nuevo un
`.exe` más reciente actualiza la copia instalada.

**Portable**: descomprime el `.zip` donde quieras (también un USB). El archivo `portable.txt` junto al `.exe` hace
que no se instale nada y que ajustes, historial y registros se guarden en la carpeta `data` de al lado.

La primera vez Windows SmartScreen puede avisar porque el ejecutable no está firmado: *Más información → Ejecutar de
todas formas*.

Las dos versiones se **actualizan solas**: comprueban nuevas versiones al minuto de arrancar y cada 12 h, verifican
el SHA-256 y se reemplazan a sí mismas (se puede desactivar en *Ajustes › General*).

## Publicar una versión

```powershell
git tag v0.3.1; git push origin v0.3.1
```

El workflow *Release* ejecuta los tests, compila el `.exe` con la dirección de actualizaciones y publica
`Konnichiwatt.exe`, `update.json` y `Konnichiwatt-portable.zip` en GitHub Releases.

## Dispositivos soportados

| Familia | Cómo se lee | Notas |
|---|---|---|
| **HyperX Cloud Alpha Wireless** | Comandos HID del dongle (`21 BB 0B`) | % exacto + estado de carga |
| **Logitech** (LIGHTSPEED / Unifying / Bolt / cable) | HID++ 2.0, features `0x1004`, `0x1000`, `0x1001` | G903 usa voltaje (`0x1001`) → % aproximado |
| **Mandos Xbox** | `Windows.Gaming.Input` (lo mismo que usa la Game Bar) | |
| **GameSir T4n Pro** (dongle 2.4 GHz `3537:103E`) | Canal HID de fabricante `FFA0`: `07 03 0A 01` → `06 0E 0A 01 … [13]=%` (`FF` = cargando) | Por XInput se presenta como mando de 360 con cable, sin batería |
| **Switch Pro / Joy-Con** y mandos que los emulan (GameSir en modo Switch) | Subcomando HID `0x02`, byte de batería del informe `0x21` | 5 niveles (≈) |
| **Bluetooth genérico** | La batería que muestra Configuración (`DEVPKEY_Bluetooth_Battery`) | Ratones/teclados/auriculares BLE |

### Sin probar con hardware real (implementados a partir de drivers open source)

| Familia | Fuente | Protocolo |
|---|---|---|
| **Razer** — ~60 ratones (Viper, DeathAdder, Basilisk, Naga, Cobra, Pro Click, Orochi, Atheris…) y teclados HyperSpeed | [OpenRazer](https://github.com/openrazer/openrazer) | Feature report 90 B, clase `07` cmd `80` (nivel 0-255) / `84` (carga), transaction ID por modelo |
| **Sony DualSense / Edge / DualShock 4** | Kernel Linux `hid-playstation.c` | Pasivo: byte de estado del informe `0x01` (USB) / `0x31`·`0x11` (BT completo) |
| **SteelSeries Arctis** 7/9/Pro Wireless/7+/Nova 7/Nova Pro Wireless | [HeadsetControl](https://github.com/Sapd/HeadsetControl) | `06 18`, `00 20`, `40 AA`, `00 B0`, `06 B0` según familia |
| **Corsair** Void (Wireless/RGB Elite/Pro), Void Wireless V2, Virtuoso Max, HS80 Max | HeadsetControl | `C9 64` / Bragi `02 09 02 0F` |
| **HyperX** Cloud II Wireless, Cloud Flight | HeadsetControl | `06 FF BB 02` / `21 FF 05` (voltaje) |
| **Logitech** G533/G633/G635/G733/G933/G935 | HeadsetControl | HID++ feature `0x1F20` (voltaje → curva) |

Solo se envían comandos de **lectura**. No incluidos: ratones/teclados Corsair (protocolo Bragi requiere cambiar a "modo software", interferiría con iCUE) y DualSense por Bluetooth si ninguna app (Steam, DS4Windows) ha activado el informe completo.

Un ratón Logitech que deja de responder (reposo) sigue mostrándose atenuado con su último valor durante 30 min.

## Uso

- **Pasar el ratón** por encima del widget → detalle por dispositivo.
- **Clic izquierdo en el widget** → refrescar ahora. **Clic derecho** → menú rápido (baterías, actualizar, ajustes, salir).
- **Clic en el icono de la bandeja** → ventana de **Ajustes**:
  - *Dispositivos*: renombrar, cambiar icono, ordenar, mostrar/ocultar, ocultar mientras cargan.
  - *Apariencia*: porcentaje / icono de batería / ambos, texto grueso, en qué pantallas aparece (con vista previa).
  - *Avisos*: batería baja (umbral ajustable), carga completa, aviso de prueba.
  - *General*: iniciar con Windows, frecuencia de lectura, idioma, actualizaciones, diagnóstico, desinstalar.
- Funciona también en la barra de tareas de monitores secundarios y en barras verticales (ExplorerPatcher / StartAllBack).

Opciones de línea de comandos: `--settings` (abre los ajustes al iniciar), `--uninstall [--quiet]`, `--diag salida.txt`,
`--preview estilos.png` (todos los estilos del widget), `--settings-shot carpeta` (capturas de la ventana de ajustes).

Ajustes: `%APPDATA%\Konnichiwatt\settings.json` · Log: `%LOCALAPPDATA%\Konnichiwatt\konnichiwatt.log` (en modo portable, todo en `data\` junto al `.exe`)

## Compilar

Requiere .NET 10 SDK.

```powershell
cd src\Konnichiwatt
dotnet build -c Release
# un solo .exe que no necesita .NET instalado (para compartir):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ..\..\publish
```

Diagnóstico sin interfaz: `Konnichiwatt.exe --diag salida.txt`

## Tests

```powershell
dotnet test Konnichiwatt.slnx
```

`tests/Konnichiwatt.Tests` comprueba la decodificación de cada protocolo (con respuestas reales capturadas del
HyperX Cloud Alpha, el G903 y el dongle del GameSir T4n Pro, y con los formatos documentados del resto), el cálculo
de la estimación de batería y la lógica de ajustes (orden, umbrales por dispositivo, modo discreto, pantallas).

## Cómo funciona el widget

Windows 11 eliminó las "deskbands", así que el widget es una ventana *layered* (alpha por píxel) hija de
`Shell_TrayWnd`, colocada a la izquierda de `TrayNotifyWnd` y reposicionada cada segundo
(cambios de ancho de la bandeja, DPI, reinicio de Explorer). Solo barra de tareas horizontal.

## Añadir un dispositivo

Implementa `IBatteryProvider` (`src/Konnichiwatt/Devices`) y regístralo en `Program.CreateProviders()`.
