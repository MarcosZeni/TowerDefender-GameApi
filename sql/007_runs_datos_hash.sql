-- Las corridas anteriores quedan sin hash: nunca se les asigna uno supuesto.
ALTER TABLE runs ADD COLUMN datos_hash char(64) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL AFTER datos_version;
