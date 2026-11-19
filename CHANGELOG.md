# Changelog de Zentra

Zentra es un fork de [optimizerDuck](https://github.com/itsfatduck/optimizerDuck) (GPL v3). Aquí solo se anotan los cambios de Zentra; los del proyecto original están en sus releases.

## [Sin publicar]

### Fase 4: reparación y almacenamiento (19/11/2026)

**Reparar Windows** (página nueva)
- Corre, en este orden: DISM `/ScanHealth`, DISM `/RestoreHealth` (solo si la imagen tiene daños; si está sana se salta), `sfc /scannow` y `chkdsk C: /scan`.
- Pasa por `OperationRunner`: diálogo de progreso con el porcentaje de cada paso, reintento si algo falla y registro en el historial. Las reparaciones quedan como irreversibles.
- La salida se lee con la codificación de cada herramienta (UTF-16 de sfc, OEM 850 de DISM y chkdsk, UTF-8), así que no salen caracteres rotos.
- El estado de la imagen se lee con `Repair-WindowsImage -CheckHealth`, que no depende del idioma. `chkdsk` se interpreta por su código de salida (0 sin errores, 1 o 2 reparó, 3 necesita reparar al reiniciar).
- Cada paso tiene un límite de 60 minutos.

**Categoría Almacenamiento** (en Optimizar)
- Limpiar el almacén de componentes (ACT-DISM-CLEANUP, Safe): `/AnalyzeComponentStore` y `/StartComponentCleanup`. Irreversible.
- Activar el Sensor de almacenamiento (ACT-STORAGESENSE, Safe). Reversible.
- Activar TRIM (ACT-TRIM, Safe): `DisableDeleteNotification = 0`. Reversible.
- Comprimir Windows (ACT-COMPACTOS, Moderate): solo en discos de sistema de 256 GB o menos. Se revierte con `compact /CompactOS:never`. Si ya estaba comprimido, se salta.
- Borrar respaldos de actualizaciones (ACT-DISM-RESETBASE, Risky): irreversible, nunca en un perfil por defecto.
- DISM y Compact OS usan un límite de 60 minutos; el del resto de la app es de 2.

**Red**
- Ajuste automático de TCP en normal (ACT-TCP-NORMAL, Safe, en Rendimiento): lee el nivel actual con `Get-NetTCPSetting` (no depende del idioma) y al revertir pone el nivel anterior.

**Diagnóstico**
- Regla R-WIN-CORRUPT: archivos de Windows dañados según DISM CheckHealth (Alta, ACT-REPAIR-WIN).

**Prueba en el PC de Diego** (19/11/2026, solo la prueba autorizada)
- Storage Sense, TRIM y TCP ya estaban bien: quedaron como "No había nada que hacer" y sin datos de reversión.
- Reversión real: con Storage Sense apagado a propósito, se aplicó (0 a 1, cambio con reversión guardada) y se revirtió (1 a 0, archivo de reversión borrado, historial con "Revert"). Al final se dejó activo, como estaba.
- Sin probar en el PC: la limpieza con DISM, Reparar Windows, Compact OS y ResetBase. Están cubiertos por tests.

### Fase 3: medición antes/después y PDF (19/11/2026)

**Qué hace**
- Página **Sesión** (debajo de Diagnóstico) con tres pasos: medir antes, medir después y generar el PDF para el cliente. El nombre del cliente es opcional, se guarda solo en el PC del técnico y nunca va a la IA.
- Cada medición corre el diagnóstico completo y mide:
  - **Arranque:** mediana de los últimos 3. Usa el evento 100 de Diagnostics-Performance y, si Windows no lo registra, el plan B: del arranque del kernel (evento 12) al inicio de sesión (Winlogon 7001). También lee los culpables de los eventos 101 a 110 cuando existen.
  - **RAM en reposo:** promedio de 60 s, tomado al menos 5 minutos después de que arrancó el escritorio.
  - Espacio libre en C:, apps de inicio activas, procesos y servicios activos.
  - **Disco con DiskSpd:** lectura secuencial y aleatoria de 15 s cada una, sin caché y solo lectura. Crea un archivo de prueba de 1 GB y lo borra.
  - Si el inicio rápido está activo, para comparar siempre en el mismo modo.
- Las sesiones se guardan en `%LocalAppData%\Zentra\Sessions\<fecha>_<id>\` (`session.json` y el PDF).
- **PDF (QuestPDF, fuente Lato incluida):**
  - Puntaje antes y después, y tabla de métricas con el cambio en verde o rojo.
  - Lo que encontramos, lo que hicimos (desde el historial de optimizaciones), lo pendiente para el cliente (BIOS, hardware y manual), lo que hace lento el arranque, lo que no se pudo medir y notas.
  - Si falla, no deja un PDF a medias.
- **Regla nueva R-BOOT-SLOW:** arranque de más de 60 s (Media), con la acción ACT-STARTUP-REVIEW.
- **Apagados inesperados:** el pendiente dice "Revisar la estabilidad del perfil XMP/EXPO", no "Activar XMP", porque el plan lo pide como revisión.

**DiskSpd**
- El binario oficial de Microsoft tiene un EULA que no permite redistribuirlo. Zentra lo compila desde el código MIT (commit 5e7025b, DISKSPD 2.3) y lo incluye en `Tools\diskspd.exe` (ver `Resources/Tools/DiskSpd/BUILD.txt`).

**Arreglos**
- 65 textos en español tenían las tildes corruptas ("DiagnÃ³stico") por los scripts de traducción de las fases 1 y 2. Están reparados y hay un test que lo evita.

**Resultado en el PC de Diego** (19/11/2026)
- Arranque 5,6 s (del kernel al inicio de sesión: el evento 100 no se registra en esta build), RAM en reposo 11,0 GB (69,9 %, con apps de trabajo abiertas), 2 apps de inicio, 243 procesos, 113 servicios.
- Crucial P3: lectura secuencial 3.517 MB/s y aleatoria 4K 178.720 IOPS.
- PDF generado: puntaje 51 con los 4 hallazgos de la Fase 2.

### Fase 2: diagnóstico (19/11/2026)

**Qué hace**
- Página **Diagnóstico** (debajo del Panel). Solo lee: no cambia nada del PC. El escaneo arranca cuando el técnico pulsa el botón, después de que el cliente lo autoriza (aviso de la Ley 1581 en la página).
- 17 reglas de la sección 7 del plan, puntaje de 0 a 100 (sección 7.1), hallazgos por severidad con su evidencia y las acciones del catálogo, y una sección "No medible" con la razón de cada dato que no se pudo leer. Lo no medible nunca resta puntos ni se inventa.
- Catálogo cerrado de las 41 acciones de la sección 6.1. Un hallazgo con una acción fuera del catálogo se descarta.
- Tablas embebidas en el `.exe`: `ram_kits.json` (tu kit y los patrones de número de parte de Kingston, G.Skill, Corsair, Crucial Ballistix y XPG), `ssd_tbw.json` (Crucial P3 500 GB y Kingston A400 480 GB) y `bios_latest.json` (ASRock B450M-HDV R4.0 = P10.10).
- El registro (`Zentra.log`) anota una línea por regla con su resultado, razón y evidencia.

**Qué lee**
- RAM: velocidad configurada y reportada por módulo, banco ("P0 CHANNEL A"), número de parte y RAM instalada (no la visible: la UMA esconde una parte).
- Gráfica: memoria dedicada (UMA) y si es integrada o dedicada.
- Discos: salud, desgaste, errores sin corregir, horas y temperatura, pidiendo el contador a cada disco (la clase no se puede listar directamente).
- BIOS (versión SMBIOS y fecha sin corrimiento de zona horaria) y placa.
- VBS, integridad de memoria, Secure Boot y activación.
- Kernel-Power 41 de los últimos 30 días (sin contar los causados por el botón de encendido), pantallazos azules y minidumps.
- Plan de energía, auto-tuning de TCP, TRIM, pagefile, Storage Sense y número de apps de inicio activas.

**Privacidad**
- El snapshot no guarda seriales, etiquetas de volumen, rutas, nombres de apps, del plan de energía, del equipo ni del usuario. Los textos de hardware se limpian y se recortan a 64 caracteres. Hay tests que lo verifican.

**Resultado en el PC de Diego** (como administrador, 19/11/2026)
- Hallazgos: memoria de la gráfica integrada en 495 MB con 16 GB (Alta), 3 apagados inesperados en 30 días (Alta), Kingston A400 con 1 error de lectura sin corregir (Crítica) y VBS activo (informativo).
- Desgaste: Crucial P3 0 %. Kingston A400: no medible (Windows reporta 0 en SATA aunque el disco esté gastado; necesita smartctl).
- RAM a 3600 igual a la del kit, BIOS P10.10 (la última estable), TRIM activo, plan Equilibrado y Windows activado.

### Fase 1: rebrand y limpieza del fork (19/11/2026)

**Marca**
- El producto se llama **Zentra** y el ejecutable es `Zentra.exe`. Los namespaces de C# siguen siendo `optimizerDuck.*` para poder traer correcciones del original.
- Ícono provisional (una "Z" sobre un degradado) en la ventana, en "Acerca de" y en el `.exe`.
- Carpeta de datos: `%LocalAppData%\Zentra` (registro `Zentra.log`, reversiones e historial).
- Punto de restauración: "Zentra Restore Point".
- "Acerca de", `THIRD-PARTY-NOTICES.md` y el README conservan los créditos a optimizerDuck y fatDuck.

**Actualizador**
- Solo busca versiones en `Dieguito44/optimizerDuck` y solo acepta ejecutables `Zentra*.exe`. Nunca ofrece una versión del original.
- Si el fork aún no tiene releases, lo anota como información y no como error.
- `release.yml` publica `Zentra-Windows-x64-<versión>.exe` y ya no notifica al Discord del original. `CODEOWNERS` apunta al fork.

**Ajustes de la sección 1.1 del plan**
- La optimización de "200+ servicios" (deshabilitaba varios y tocaba `wuauserv` y `WaaSMedicSvc`) se reemplazó por **Servicios poco usados en Manual** (ACT-SERVICES-MANUAL): Fax, MapsBroker, RetailDemo, wisvc y WMPNetworkSvc pasan a Manual, nunca a Deshabilitado. Los que no existen se saltan.
- Se quitó **MaximizeUpdatePauseLimit**: permitía pausar Windows Update sin límite. La pausa controlada de 7 días (ACT-WU-PAUSE7) llega en la Fase 7.
- El README ya no recomienda apagar Windows Update.
- **Desactivar hibernación e inicio rápido** ahora dice que es opcional y que no hace parte del perfil estándar.
- La advertencia del 100 % de CPU con el plan de energía propio (issue #29 del original) ya estaba en inglés y en español.

**Bloatware**
- Edge, WebView2, la Store, winget (DesktopAppInstaller), Seguridad de Windows, la Calculadora, Fotos, VCLibs, UI.Xaml, .NET Native y Windows App Runtime están protegidos. No aparecen en la lista y quitarlos se rechaza aunque alguien lo intente.

**Idioma**
- La app arranca en español (`es-ES`). Se corrigieron tildes y textos de marca.

**Tests**
- `BlacklistTests`: 19 reglas sobre el código fuente de la app (Defender, Windows Update, UAC, firewall, SmartScreen, BitLocker, Smart App Control, bcdedit, auto-tuning de TCP, pagefile, HOSTS, Edge/WebView2, liberadores de RAM y activadores). Cada regla se prueba contra ejemplos malos y deja pasar las variantes controladas.
- Lista blanca de los 6 servicios que hoy se ponen en Disabled (cualquier otro hace fallar el test).
- Tests del actualizador (apunta al fork e ignora ejecutables del original) y de las apps protegidas.
- Total: 844 tests, 0 fallos, 6 omitidos (requieren correr como administrador o un estado concreto del PC).
