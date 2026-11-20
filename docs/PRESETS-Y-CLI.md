# Presets y línea de comandos

Plan de mejoras F3-01 y F3-02. Sirven para que todos los clientes reciban el mismo servicio, y para atender varios equipos con menos clics.

## Presets

Un preset es un archivo JSON con:
- un nombre;
- la lista de ajustes (por su GUID);
- las apps que se quitan.

El **Servicio estándar** es el preset que trae Zentra.

```json
{
  "schema": 1,
  "name": "Servicio estándar",
  "zentraVersion": "1.0.0",
  "created": "2026-11-19",
  "allowRisky": false,
  "settings": ["962c9960-6bfa-4fc5-97d9-bfe772426e39"],
  "apps": ["Microsoft.BingNews"],
  "appFragments": ["CandyCrushSaga"]
}
```

- **Crear uno:** en Sesión, "Exportar preset" guarda el preset activo. Edita el archivo y luego impórtalo.
- **Importar:** en Sesión, "Importar preset". Desde ese momento, "Aplicar" y "Revertir" usan ese preset. "Volver al Servicio estándar" deshace el cambio.
- **`apps`** coincide con el nombre exacto del paquete.
- **`appFragments`** coincide con cualquier paquete que contenga el texto. Necesita al menos 6 letras.

Zentra rechaza el archivo y dice por qué si:
- tiene un campo que no conoce, o un GUID que no existe en esa versión;
- repite un ajuste o una app;
- trae ajustes de nivel Experto sin `"allowRisky": true`;
- nombra una app protegida (la Tienda, Fotos, la Calculadora, etc.);
- pesa más de 256 KB.

Las apps protegidas nunca se quitan, aunque un fragmento coincida.

## Línea de comandos

Como administrador:

```powershell
$p = Start-Process .\Zentra.exe -ArgumentList '--preset','estandar','--diagnose','--report','D:\Clientes\Ana','--client','Ana' -Wait -NoNewWindow -PassThru
$p.ExitCode
```

`Start-Process -Wait` hace falta porque Zentra es una app de ventanas: sin él, PowerShell no espera a que termine.

| Opción | Qué hace |
|---|---|
| `--diagnose` | Mide y diagnostica, como "Medir antes". Con `--preset`, mide antes y después. |
| `--preset <archivo\|estandar>` | Aplica un preset. |
| `--report <carpeta>` | Copia `session.json`, el PDF y `zentra-cli.txt` a esa carpeta. |
| `--client <nombre>` | Nombre del cliente para el informe. Queda solo en este PC. |
| `--silent` | No pide confirmación. |
| `--keep-apps` | Aplica los ajustes sin quitar las apps del preset. |
| `--allow-risky` | Permite ajustes de nivel Experto. El preset también debe tener `allowRisky`. |
| `--help` | Muestra la ayuda. |

Con preset, el orden siempre es el mismo:
1. Valida el preset.
2. Muestra la vista previa (cada valor: ahora → después).
3. Pide confirmación en una ventana, salvo con `--silent`.
4. Crea el punto de restauración. Si no se puede crear, no se aplica nada.
5. Mide antes.
6. Aplica.
7. Mide después.
8. Escribe el informe.

### Códigos de salida

| Código | Significado |
|---|---|
| 0 | Listo. |
| 1 | Argumentos no válidos. No se cambió nada. |
| 2 | Preset no válido o inexistente, o con ajustes Experto sin `--allow-risky`. |
| 3 | No corre como administrador. |
| 4 | Sin punto de restauración. No se aplicó nada. |
| 5 | Se aplicó, pero algunos ajustes o apps fallaron (el informe los nombra). |
| 6 | Cancelado en la confirmación. |
| 10 | Error inesperado (ver el registro de Zentra). |
