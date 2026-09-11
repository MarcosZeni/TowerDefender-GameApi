-- --------------------------------------------------------
-- Host:                         127.0.0.1
-- Versión del servidor:         8.0.33 - MySQL Community Server - GPL
-- SO del servidor:              Win64
-- HeidiSQL Versión:             12.17.0.7270
-- --------------------------------------------------------

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET NAMES utf8 */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;


-- Volcando estructura de base de datos para towerdef
DROP DATABASE IF EXISTS `towerdef`;
CREATE DATABASE IF NOT EXISTS `towerdef` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;
USE `towerdef`;

-- Volcando estructura para tabla towerdef.cuentas
DROP TABLE IF EXISTS `cuentas`;
CREATE TABLE IF NOT EXISTS `cuentas` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `nick` varchar(20) NOT NULL,
  `password` varchar(255) NOT NULL,
  `email` varchar(120) DEFAULT NULL,
  `oro` bigint unsigned NOT NULL DEFAULT '0',
  `ultima_ip` varbinary(16) DEFAULT NULL,
  `creada` datetime(3) NOT NULL,
  `ultimo_acceso` datetime(3) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_cuentas_nick` (`nick`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- La exportación de datos fue deseleccionada.

-- Volcando estructura para tabla towerdef.intentos_login
DROP TABLE IF EXISTS `intentos_login`;
CREATE TABLE IF NOT EXISTS `intentos_login` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `ip` varbinary(16) DEFAULT NULL,
  `nick` varchar(20) DEFAULT NULL,
  `exito` tinyint unsigned NOT NULL DEFAULT '0',
  `cuando` datetime(3) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_intentos_ip` (`ip`,`cuando`),
  KEY `idx_intentos_nick` (`nick`,`exito`,`cuando`),
  KEY `idx_intentos_cuando` (`cuando`)
) ENGINE=InnoDB AUTO_INCREMENT=14 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- La exportación de datos fue deseleccionada.

-- Volcando estructura para tabla towerdef.planos_cuenta
DROP TABLE IF EXISTS `planos_cuenta`;
CREATE TABLE IF NOT EXISTS `planos_cuenta` (
  `id_cuenta` bigint unsigned NOT NULL,
  `plano` varchar(48) NOT NULL,
  `precio` int unsigned NOT NULL,
  `comprado` datetime(3) NOT NULL,
  PRIMARY KEY (`id_cuenta`,`plano`),
  CONSTRAINT `fk_planos_cuenta` FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- La exportación de datos fue deseleccionada.

-- Volcando estructura para tabla towerdef.runs
DROP TABLE IF EXISTS `runs`;
CREATE TABLE IF NOT EXISTS `runs` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `id_cuenta` bigint unsigned NOT NULL,
  `seed` bigint unsigned NOT NULL,
  `gano` tinyint unsigned NOT NULL DEFAULT '0',
  `oleada` tinyint unsigned NOT NULL DEFAULT '0',
  `area` tinyint unsigned NOT NULL DEFAULT '1',
  `ticks` int unsigned NOT NULL DEFAULT '0',
  `motivo_cierre` enum('perdio','gano','abandono') DEFAULT NULL,
  `datos_version` smallint unsigned NOT NULL DEFAULT '1',
  `log_formato` tinyint unsigned NOT NULL DEFAULT '1',
  `log_truncado` tinyint unsigned NOT NULL DEFAULT '0',
  `log_acciones` mediumblob,
  -- 0 = pendiente (lo que escribe /run/finish: lo del cliente es un reclamo, no
  -- un hecho), 1 = la re-simulacion coincide, 2 = no coincide, 3 = el log no se
  -- puede leer. El 3 existe para SACAR la fila de la cola: sin el, una fila con
  -- el blob cortado o vacio se queda en 0 y el revalidador la vuelve a tomar en
  -- cada lote, para siempre. No es lo mismo que un 2: el 2 acusa al jugador.
  `validado` tinyint unsigned NOT NULL DEFAULT '0',
  `iniciada` datetime(3) NOT NULL,
  `terminada` datetime(3) DEFAULT NULL,
  `tick_guardado` int unsigned NOT NULL DEFAULT '0',
  `actualizada` datetime(3) DEFAULT NULL,
  `plataforma` enum('pc','celular') NOT NULL DEFAULT 'pc',
  PRIMARY KEY (`id`),
  KEY `idx_runs_cuenta_abierta` (`id_cuenta`,`motivo_cierre`),
  KEY `idx_runs_ranking` (`datos_version`,`gano`,`oleada`,`ticks`),
  CONSTRAINT `fk_runs_cuenta` FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=11 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- La exportación de datos fue deseleccionada.

-- Volcando estructura para tabla towerdef.tokens
DROP TABLE IF EXISTS `tokens`;
CREATE TABLE IF NOT EXISTS `tokens` (
  `token_hash` binary(32) NOT NULL,
  `id_cuenta` bigint unsigned NOT NULL,
  `creado` datetime(3) NOT NULL,
  `expira` datetime(3) NOT NULL,
  `ip` varbinary(16) DEFAULT NULL,
  PRIMARY KEY (`token_hash`),
  KEY `idx_tokens_cuenta` (`id_cuenta`),
  KEY `idx_tokens_expira` (`expira`),
  CONSTRAINT `fk_tokens_cuenta` FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- La exportación de datos fue deseleccionada.

/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;
