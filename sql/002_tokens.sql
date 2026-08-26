-- =============================================================================
--  002 — Tokens de sesión
--
--  Un token opaco de 32 bytes de CSPRNG por sesión iniciada.
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < 002_tokens.sql
--
--  ⚠ Diferencia importante con el LoginServer de Austral: allá el token se EMITE
--    y nunca se canjea —lo canjea el server de juego, que es otro proceso—. Acá
--    esta misma API lo valida en cada request, así que la lógica de canje hay
--    que escribirla de cero:
--
--        Authorization: Bearer <token>  ->  LoginToken.HashOf  ->  lookup por PK
--        ->  chequeo de `expira`
-- =============================================================================


CREATE TABLE IF NOT EXISTS `tokens` (

    -- ⚠ Se guarda el SHA-256 del token, NO el token.
    --
    -- Quien lea esta tabla no puede usar nada de lo que hay adentro. Y el hash
    -- rápido acá está bien, aunque las contraseñas usen Argon2id: el costo del
    -- hash lento existe para compensar la poca entropía de lo que elige una
    -- persona. Contra 256 bits de aleatoriedad real no hay diccionario ni fuerza
    -- bruta que sirva, por veloz que sea el algoritmo.
    --
    -- Como PK: el lookup de cada request es exactamente esta búsqueda, así que
    -- es el índice agrupado y no hay una indirección de más.
    `token_hash`   BINARY(32)       NOT NULL,

    `id_cuenta`    BIGINT UNSIGNED  NOT NULL,

    `creado`       DATETIME(3)      NOT NULL,

    -- Cuándo deja de valer. Lo compara la validación en cada request.
    --
    -- ⚠ Esto es del TOKEN, no de la corrida. Una corrida NO caduca por tiempo:
    --   se cierra al perder, al ganar, o cuando el jugador abre otra. Confundir
    --   las dos cosas rechazaría un finish después de tres horas bien jugadas,
    --   que es lo peor que puede hacer este servidor.
    `expira`       DATETIME(3)      NOT NULL,

    `ip`           VARBINARY(16)    NULL,

    PRIMARY KEY (`token_hash`),

    -- Para el logout de todas las sesiones y para el barrido de vencidos.
    KEY `idx_tokens_cuenta` (`id_cuenta`),
    KEY `idx_tokens_expira` (`expira`),

    CONSTRAINT `fk_tokens_cuenta`
        FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`)
        ON DELETE CASCADE

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
