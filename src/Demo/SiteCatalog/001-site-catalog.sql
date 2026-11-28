CREATE TABLE IF NOT EXISTS cassini_site_catalog (
    owner text PRIMARY KEY,
    snapshot jsonb NOT NULL
);
