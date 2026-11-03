/** Rechnet die möglichen Angaben zu einer Dachfläche in die Werte um, die der Server speichert. */

export const DIRECTIONS = [
  ["0", "Süd"], ["45", "Südwest"], ["90", "West"], ["135", "Nordwest"],
  ["180", "Nord"], ["-135", "Nordost"], ["-90", "Ost"], ["-45", "Südost"],
];

export const TILTS = [["10", "Flach, um 10°"], ["30", "Normal, um 30°"], ["45", "Steil, um 45°"]];

/** Kompass: 0 = Nord, 90 = Ost, 180 = Süd. Gespeichert wird 0 = Süd, -90 = Ost, 90 = West. */
export const compassToAzimuth = compass => (compass % 360 + 360) % 360 - 180;

export const modulesToKwp = (count, watt) => Math.round(count * watt) / 1000;

/** Wählt beim Laden die Auswahl, die den gespeicherten Wert genau trifft. */
export const directionOption = azimuth => azimuth === -180 ? "180" : DIRECTIONS.some(([v]) => Number(v) === azimuth) ? String(azimuth) : "degrees";

export const tiltOption = tilt => TILTS.some(([v]) => Number(v) === tilt) ? String(tilt) : "degrees";
