CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

CREATE TABLE IF NOT EXISTS trading_windows (
    symbol_id VARCHAR(32) NOT NULL,
    window_start TIMESTAMPTZ NOT NULL,
    window_end TIMESTAMPTZ NOT NULL,
    ema_38 DOUBLE PRECISION NOT NULL,
    ema_100 DOUBLE PRECISION NOT NULL,
    last_price DOUBLE PRECISION NOT NULL,
    buy_signal BOOLEAN DEFAULT FALSE,
    sell_signal BOOLEAN DEFAULT FALSE,
    PRIMARY KEY (symbol_id, window_start)
);

SELECT create_hypertable('trading_windows', 'window_start', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS idx_trading_windows_symbol_time 
ON trading_windows (symbol_id, window_start DESC);
