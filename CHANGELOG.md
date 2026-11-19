# Changelog de Zentra

Zentra es un fork de [optimizerDuck](https://github.com/itsfatduck/optimizerDuck) (GPL v3). Aquí solo se anotan los cambios de Zentra; los del proyecto original están en sus releases.

## [Sin publicar]

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
