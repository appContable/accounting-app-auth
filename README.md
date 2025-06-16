# Accounting App · Auth API
Authentication and authorization backend for the Accounting App. Manages login for end-users using JWT tokens.

## Tech Stack
- Node.js
- TypeScript
- Express
- JWT (jsonwebtoken)
- bcrypt for password hashing

## Structure
- `controllers/`: auth handlers
- `services/`: user and token logic
- `models/`: user schema & interfaces
- `middlewares/`: auth guards and validators
- `routes/`: login and token endpoints

## Auth Flow
- `POST /auth/login`: user login
- `GET /auth/me`: validate JWT token
- (No user registration via UI — users created via script)
