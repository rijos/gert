// components/ui/loader.js — spinning loader (.loader).
import van from "van";

const { span } = van.tags;

export const Loader = () => span({ class: "loader" });
