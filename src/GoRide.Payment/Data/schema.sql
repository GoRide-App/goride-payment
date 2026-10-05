CREATE TABLE IF NOT EXISTS payments (
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    document JSON NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS payment_checkouts (
    id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    idempotency_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    amount_minor BIGINT NOT NULL,
    currency CHAR(3) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    success_url TEXT NOT NULL,
    cancel_url TEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    provider_session_id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NULL UNIQUE,
    INDEX ix_checkout_trip (trip_id, id),
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS processed_payment_events (
    event_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    payload_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    processed_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX ix_payment_events_trip (trip_id)
) ENGINE=InnoDB;
